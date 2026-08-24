// ============================================================================
// SummaryScheduler.cs — AI 总结定时调度器
// 职责：后台线程每日 0:00 触发"昨日日总结 + 上周周总结 + 上月月总结"；
//       Start 时补算错过的任务；RegenerateNow 供设置变更后立即重算。
// ============================================================================
// —— 命名空间导入：定时器/线程原语 / 异步任务 / 数据仓储 / 日期助手 ——
using System;
using System.Threading;
using System.Threading.Tasks;
using TimeActivity.Data;
using TimeActivity.Helpers;

namespace TimeActivity.Services;

/// <summary>
/// AI 总结定时调度（日 / 周 / 月 三维度）— 统一在每天 0:00 触发对"刚结束的周期"的总结，并在启动时补算错过的任务。
///
/// 一、三个维度的周期与触发点
/// - 日（daily）：每天 0:00 为"昨天"生成日报；若程序错过若干天，启动 / 每次 0:00 时补算最近 7 天。
/// - 周（weekly）：每周一 0:00 为"上周（周一~周日）"生成周报；其余日子若发现上周周报缺失也会补算。
/// - 月（monthly）：每月 1 号 0:00 为"上月"生成月报；其余日子若发现上月月报缺失也会补算。
///   注：一周以周一为起点（符合国内习惯），由 DateHelper.GetLatestClosedWeekStart / GetLatestClosedMonthStart 计算"最近一个已结束周期"。
///
/// 二、幂等与去重
/// - 唯一索引 (Date, SummaryType, AutoType) 保证同周期同来源只存一条；
/// - 生成前用 AISummaryRepository.HasAuto(周期, 类型) 判断"是否已有自动总结"，已有则跳过，绝不重复生成。
///
/// 三、开关
/// - AutoDailySummary / AutoWeeklySummary / AutoMonthlySummary（默认 true）与 EnableAI（默认 true）共同控制是否自动生成。
///
/// 四、实现细节
/// - 后台线程：System.Threading.Timer 触发，AI 调用是网络 I/O，不阻塞 UI。
/// - 一次性排程 + 每次触发后重新排到下一个 0:00（period=Infinite），避免间隔漂移，天然兼容夏令时 / 手动改时钟。
/// - 启动补算（Backfill）与每日起夜触发共用同一份 GenerateMissingAsync，逻辑单一、无歧义。
/// </summary>
public class SummaryScheduler
{
    // 后台定时器；用一次性触发 + 每次重新排程的方式，避免间隔漂移、也天然兼容夏令时/手动改时钟
    private System.Threading.Timer? _timer;
    // 运行标志：Start 后为 true、Stop 后为 false，用于让迟到的定时器回调自行退出
    private bool _running;

    // 启动/补算时最多回补最近 7 天日报，避免冷启动（程序长期没开）猛调一堆 AI
    private const int DailyBackfillDays = 7;

    // ==================== 启停与手动触发 ====================

    /// <summary>
    /// 启动调度：先补算所有错过的日/周/月总结，再排到下一个 0:00。
    /// </summary>
    public void Start()
    {
        // 幂等保护：已在运行则忽略重复启动
        if (_running) return;
        _running = true;

        // 启动即补：把错过的总结任务一次性补齐（程序可能没在 0:00 运行）
        // fire-and-forget：GenerateMissingAsync 内部整体 try/catch 兜底，不会有未观察异常
        _ = GenerateMissingAsync();

        // 排定下一个 0:00 的定时触发
        ScheduleNext();
        Logger.Info("AI 总结定时调度已启动（每天 0:00 检查 日/周/月）");
    }

    /// <summary>
    /// 停止调度并释放定时器（真正退出程序时调用）。
    /// </summary>
    public void Stop()
    {
        // 先摘运行标志：进行中的生成不受影响，但迟到的定时器回调会自行放弃
        _running = false;
        // 释放并清空定时器，取消尚未到点的触发
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 立即补算一次（供"数据变更后需要刷新总结"的场景调用，fire-and-forget，不阻塞调用方）。
    /// </summary>
    public void RegenerateNow() => _ = GenerateMissingAsync();

    // ==================== 定时排程 ====================

    /// <summary>
    /// 排程到下一个 0:00 触发一次（period=Infinite，触发后再重新排，避免漂移）。
    /// </summary>
    private void ScheduleNext()
    {
        // 已停止就不再排程
        if (!_running) return;

        // 用本地时间计算"下一个 0:00"，与用户对"一天"的感知一致
        var now = DateTime.Now;
        // 今天 0:00 加一天 = 下一个 0:00
        var nextMidnight = now.Date.AddDays(1);
        var due = nextMidnight - now;
        // 防御：极边界（due 极小或为负）时至少等 1 秒，避免误触成的密集触发
        if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);

        // 一次性模式：每次触发前丢弃旧定时器、新建一个只触发一次的定时器
        _timer?.Dispose();
        // period=InfiniteTimeSpan 表示不重复；OnTick 里负责再次排程
        _timer = new System.Threading.Timer(_ => OnTick(), null, due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 定时器回调：补算错过的总结，再重新排到下一个 0:00。
    /// </summary>
    private void OnTick()
    {
        // Stop 之后仍可能收到迟到的回调，直接忽略
        if (!_running) return;
        // 触发本轮的缺失总结生成（fire-and-forget，内部有兜底）
        _ = GenerateMissingAsync();
        // 重新排程到下一个 0:00（一次性触发模式的核心）
        ScheduleNext();
    }

    // ==================== 生成逻辑 ====================

    /// <summary>
    /// 生成所有"应存在但缺失"的总结。被启动补算、每日起夜触发、数据变更后刷新共用，天然幂等（HasAuto 去重）。
    /// 失败只记日志不中断。
    /// </summary>
    private async Task GenerateMissingAsync()
    {
        try
        {
            // 整体兜底：任何异常只记日志，保证调度器后台流程不被打断

            // AI 总开关没开就整段跳过
            if (SettingsRepository.Get("EnableAI", "true") != "true") return;

            // —— 日报：扫描最近 N 天，缺失则生成"那天"的日报 ——
            // 日报自动生成开关
            if (SettingsRepository.Get("AutoDailySummary", "true") == "true")
            {
                // 以今天为基准往回扫
                var today = DateTime.Today;
                // i 从 1 开始：昨天、前天……最多回补 DailyBackfillDays 天
                for (int i = 1; i <= DailyBackfillDays; i++)
                {
                    // 本轮待补算的具体日期
                    var day = today.AddDays(-i);
                    // 已存在该日自动总结则跳过（幂等去重，绝不重复调 AI）
                    if (AISummaryRepository.HasAuto(day, "daily")) continue;
                    // 调 AI 生成当日总结（返回 null 表示失败/未启用/无数据占位文案除外）
                    var text = await new AISummaryService().GenerateDailySummary(day);
                    if (text != null)
                    {
                        // 入库：类型 daily、来源 auto，受唯一索引保护
                        AISummaryRepository.Insert(day, text, "daily", "auto");
                        Logger.Info($"已自动生成每日总结：{day:yyyy-MM-dd}");
                    }
                }
            }

            // —— 周报：最近一个完整周（上周一~上周日），缺失则生成 ——
            // 周报自动生成开关
            if (SettingsRepository.Get("AutoWeeklySummary", "true") == "true")
            {
                // 最近一个已结束自然周的周一（周一~周日口径）
                var ws = DateHelper.GetLatestClosedWeekStart();
                // 上周周报已存在则跳过
                if (!AISummaryRepository.HasAuto(ws, "weekly"))
                {
                    // 调 AI 生成周总结
                    var text = await new AISummaryService().GenerateWeeklySummary(ws);
                    if (text != null)
                    {
                        // 入库：类型 weekly、来源 auto
                        AISummaryRepository.Insert(ws, text, "weekly", "auto");
                        Logger.Info($"已自动生成每周总结：{ws:yyyy-MM-dd}（当周周一）");
                    }
                }
            }

            // —— 月报：最近一个完整月（上月），缺失则生成 ——
            // 月报自动生成开关
            if (SettingsRepository.Get("AutoMonthlySummary", "true") == "true")
            {
                // 最近一个已结束自然月的月初 1 号
                var ms = DateHelper.GetLatestClosedMonthStart();
                // 上月月报已存在则跳过
                if (!AISummaryRepository.HasAuto(ms, "monthly"))
                {
                    // 调 AI 生成月总结
                    var text = await new AISummaryService().GenerateMonthlySummary(ms);
                    if (text != null)
                    {
                        // 入库：类型 monthly、来源 auto
                        AISummaryRepository.Insert(ms, text, "monthly", "auto");
                        Logger.Info($"已自动生成每月总结：{ms:yyyy-MM}（当月月初）");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 任一环节抛异常（含唯一索引冲突）都到此为止：
            // 注意这意味着一次失败会中止本次调用中剩余日期/维度的补算
            Logger.Error("自动生成 AI 总结失败", ex);
        }
    }
}
