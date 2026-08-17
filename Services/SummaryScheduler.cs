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
    private bool _running;

    // 启动/补算时最多回补最近 7 天日报，避免冷启动（程序长期没开）猛调一堆 AI
    private const int DailyBackfillDays = 7;

    /// <summary>
    /// 启动调度：先补算所有错过的日/周/月总结，再排到下一个 0:00。
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        // 启动即补：把错过的总结任务一次性补齐（程序可能没在 0:00 运行）
        _ = GenerateMissingAsync();

        ScheduleNext();
        Logger.Info("AI 总结定时调度已启动（每天 0:00 检查 日/周/月）");
    }

    /// <summary>
    /// 停止调度并释放定时器（真正退出程序时调用）。
    /// </summary>
    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 立即补算一次（供"数据变更后需要刷新总结"的场景调用，fire-and-forget，不阻塞调用方）。
    /// </summary>
    public void RegenerateNow() => _ = GenerateMissingAsync();

    /// <summary>
    /// 排程到下一个 0:00 触发一次（period=Infinite，触发后再重新排，避免漂移）。
    /// </summary>
    private void ScheduleNext()
    {
        if (!_running) return;

        var now = DateTime.Now;
        // 今天 0:00 加一天 = 下一个 0:00
        var nextMidnight = now.Date.AddDays(1);
        var due = nextMidnight - now;
        // 防御：极边界（due 极小或为负）时至少等 1 秒，避免误触成的密集触发
        if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);

        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => OnTick(), null, due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 定时器回调：补算错过的总结，再重新排到下一个 0:00。
    /// </summary>
    private void OnTick()
    {
        if (!_running) return;
        _ = GenerateMissingAsync();
        ScheduleNext();
    }

    /// <summary>
    /// 生成所有"应存在但缺失"的总结。被启动补算、每日起夜触发、数据变更后刷新共用，天然幂等（HasAuto 去重）。
    /// 失败只记日志不中断。
    /// </summary>
    private async Task GenerateMissingAsync()
    {
        try
        {
            // AI 总开关没开就整段跳过
            if (SettingsRepository.Get("EnableAI", "true") != "true") return;

            // —— 日报：扫描最近 N 天，缺失则生成"那天"的日报 ——
            if (SettingsRepository.Get("AutoDailySummary", "true") == "true")
            {
                var today = DateTime.Today;
                for (int i = 1; i <= DailyBackfillDays; i++)
                {
                    var day = today.AddDays(-i);
                    if (AISummaryRepository.HasAuto(day, "daily")) continue;
                    var text = await new AISummaryService().GenerateDailySummary(day);
                    if (text != null)
                    {
                        AISummaryRepository.Insert(day, text, "daily", "auto");
                        Logger.Info($"已自动生成每日总结：{day:yyyy-MM-dd}");
                    }
                }
            }

            // —— 周报：最近一个完整周（上周一~上周日），缺失则生成 ——
            if (SettingsRepository.Get("AutoWeeklySummary", "true") == "true")
            {
                var ws = DateHelper.GetLatestClosedWeekStart();
                if (!AISummaryRepository.HasAuto(ws, "weekly"))
                {
                    var text = await new AISummaryService().GenerateWeeklySummary(ws);
                    if (text != null)
                    {
                        AISummaryRepository.Insert(ws, text, "weekly", "auto");
                        Logger.Info($"已自动生成每周总结：{ws:yyyy-MM-dd}（当周周一）");
                    }
                }
            }

            // —— 月报：最近一个完整月（上月），缺失则生成 ——
            if (SettingsRepository.Get("AutoMonthlySummary", "true") == "true")
            {
                var ms = DateHelper.GetLatestClosedMonthStart();
                if (!AISummaryRepository.HasAuto(ms, "monthly"))
                {
                    var text = await new AISummaryService().GenerateMonthlySummary(ms);
                    if (text != null)
                    {
                        AISummaryRepository.Insert(ms, text, "monthly", "auto");
                        Logger.Info($"已自动生成每月总结：{ms:yyyy-MM}（当月月初）");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("自动生成 AI 总结失败", ex);
        }
    }
}
