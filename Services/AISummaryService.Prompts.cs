using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TimeActivity.Data;
using TimeActivity.Helpers;

namespace TimeActivity.Services;

/// <summary>
/// AISummaryService 的“周/月总结与提示词构建”分部 — 负责周/月总结的生成与对应 prompt 拼接。
/// 与 AISummaryService.cs 同属一个 partial class，公开 API 完全不变。
/// </summary>
public partial class AISummaryService
{
    /// <summary>
    /// 生成周总结。拉一周的数据拼 prompt 发给 AI。
    /// </summary>
    /// <param name="weekStart">周一日期</param>
    /// <returns>AI 生成的 Markdown 周总结，未启用或无数据返回 null</returns>
    public async Task<string?> GenerateWeeklySummary(DateTime weekStart)
    {
        if (!Enabled) return null; // AI 未启用直接返回 null
        DateTime weekEnd = weekStart.AddDays(6); // 周一 +6 天 = 周日
        var catSummary = ActivityRepository.GetCategorySummaryByRange(weekStart, weekEnd, false);  // 分类时长（排除空闲）
        var procSummary = ActivityRepository.GetProcessSummaryByRange(weekStart, weekEnd, false);  // 应用时长
        var dailyTotals = ActivityRepository.GetDailyTotalsByRange(weekStart, weekEnd, false);     // 每日总量

        int totalSeconds = catSummary.Values.Sum();
        if (totalSeconds == 0) // 整周无数据：返回固定文案
            return "本周没有活动记录。";

        string prompt = BuildWeeklyPrompt(weekStart, weekEnd, catSummary, procSummary, dailyTotals);
        return await CallAIInternal(prompt);
    }

    /// <summary>
    /// 生成月总结。拉一个月的数据拼 prompt 发给 AI。
    /// </summary>
    /// <param name="monthStart">月初日期</param>
    /// <returns>AI 生成的 Markdown 月总结，未启用或无数据返回 null</returns>
    public async Task<string?> GenerateMonthlySummary(DateTime monthStart)
    {
        if (!Enabled) return null; // AI 未启用直接返回 null
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1); // 下月 1 号 -1 天 = 本月末
        var catSummary = ActivityRepository.GetCategorySummaryByRange(monthStart, monthEnd, false);  // 分类时长
        var procSummary = ActivityRepository.GetProcessSummaryByRange(monthStart, monthEnd, false);  // 应用时长
        var dailyTotals = ActivityRepository.GetDailyTotalsByRange(monthStart, monthEnd, false);     // 每日总量

        int totalSeconds = catSummary.Values.Sum();
        if (totalSeconds == 0) // 整月无数据：返回固定文案
            return "本月没有活动记录。";

        string prompt = BuildMonthlyPrompt(monthStart, monthEnd, catSummary, procSummary, dailyTotals);
        return await CallAIInternal(prompt);
    }

    /// <summary>
    /// 拼接周总结 prompt：包含分类时长、Top 10 应用、活跃天数、每日明细。
    /// 采用带标签的结构化写法（参考 xiaohei-daily-backend），意图明确、便于模型遵循。
    /// </summary>
    private string BuildWeeklyPrompt(DateTime weekStart, DateTime weekEnd,
        Dictionary<string, int> catSummary, Dictionary<string, int> procSummary,
        Dictionary<string, int> dailyTotals)
    {
        int activeDays = dailyTotals.Count(d => d.Value > 0);            // 有活跃的日期数
        long totalSeconds = dailyTotals.Sum(d => (long)d.Value);         // 周总活跃秒数
        long avg = activeDays > 0 ? totalSeconds / activeDays : 0;       // 活跃日均值

        var catLines = string.Join("\n", catSummary.Select(c => $"- {c.Key}: {TimeFormatHelper.Format(c.Value)}")); // 分类行
        var topLines = string.Join("\n", procSummary.Take(10)      // 只取前 10 名应用
            .Select((p, idx) => $"{idx + 1}. {p.Key}: {TimeFormatHelper.Format(p.Value)}"));
        var dailyLines = string.Join("\n", dailyTotals.Select(d => $"- {d.Key}: {TimeFormatHelper.Format(d.Value)}")); // 逐日行

        var sb = new StringBuilder();
        sb.AppendLine("请基于以下统计数据，生成【本周】时间使用总结。\n");
        sb.AppendLine($"【日期范围】{weekStart:MM-dd} ~ {weekEnd:MM-dd}（周一至周日）");
        sb.AppendLine($"【活跃天数】{activeDays}/7");
        sb.AppendLine($"【总活跃时长】{TimeFormatHelper.Format(totalSeconds)}");
        sb.AppendLine($"【日均活跃时长】{TimeFormatHelper.Format(avg)}\n");

        sb.AppendLine("【分类时长】");
        sb.AppendLine(catLines);
        sb.AppendLine("\n【Top 10 应用】（务必完整列出全部 10 个，按时长降序；不足则列实际数量）");
        sb.AppendLine(topLines);
        sb.AppendLine("\n【每日明细】");
        sb.AppendLine(dailyLines);

        sb.AppendLine("\n输出要求（使用 Markdown，结构完整，整体约 400~700 字）：");
        sb.AppendLine("## 概览\n2~3 句话概括本周时间使用全貌。");
        sb.AppendLine("## 分类时长分析\n对各分类占比做解读，点明主要投入方向与可能的失衡。");
        sb.AppendLine("## Top 10 应用亮点\n挑 2~3 个最具代表性的应用，说明其反映的工作/娱乐重心。");
        sb.AppendLine("## 日均时长与活跃天数\n结合上述数据评价节奏是否健康、是否有明显空缺或过载。");
        sb.AppendLine("## 建议与改进\n给出 2~3 条具体、可执行的改进建议。");

        sb.AppendLine("\n注意：仅依据上述数据，不要虚构；数据有限处明确说明。");
        return sb.ToString();
    }

    /// <summary>
    /// 拼接月总结 prompt：包含分类时长、Top 15 应用、活跃天数、每周对比（按周一为起点）。
    /// 采用带标签的结构化写法（参考 xiaohei-daily-backend），意图明确、便于模型遵循。
    /// </summary>
    private string BuildMonthlyPrompt(DateTime monthStart, DateTime monthEnd,
        Dictionary<string, int> catSummary, Dictionary<string, int> procSummary,
        Dictionary<string, int> dailyTotals)
    {
        int activeDays = dailyTotals.Count(d => d.Value > 0);           // 活跃天数
        long totalSeconds = dailyTotals.Sum(d => (long)d.Value);        // 月总活跃秒数
        int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month); // 当月总天数
        long avg = activeDays > 0 ? totalSeconds / activeDays : 0;      // 活跃日均值

        var catLines = string.Join("\n", catSummary.Select(c => $"- {c.Key}: {TimeFormatHelper.Format(c.Value)}"));
        var topLines = string.Join("\n", procSummary.Take(15)       // 月报取前 15 名应用
            .Select((p, idx) => $"{idx + 1}. {p.Key}: {TimeFormatHelper.Format(p.Value)}"));

        // 每周对比：按自然周、周一为起点分组，与全局"周以周一为起点"口径一致
        DateTime MondayOf(DateTime d) // 局部函数：求某日所在周的周一
        {
            int dow = (int)d.DayOfWeek;
            if (dow == 0) dow = 7;   // 周日按 7 处理
            return d.AddDays(-(dow - 1));
        }
        var monthMonday = MondayOf(monthStart); // 本月第一个周一（可能在上月）
        var weekGroups = dailyTotals
            .Select(d =>
            {
                var dt = DateTime.Parse(d.Key, CultureInfo.InvariantCulture);         // "yyyy-MM-dd" → 日期
                int weekIdx = (MondayOf(dt) - monthMonday).Days / 7 + 1;              // 第几个自然周
                return (Week: weekIdx, Seconds: (long)d.Value);
            })
            .GroupBy(x => x.Week)
            .OrderBy(g => g.Key);
        var weekLines = string.Join("\n", weekGroups
            .Select(g => $"- 第{g.Key}周: {TimeFormatHelper.Format(g.Sum(x => x.Seconds))}")); // 每周合计行

        var sb = new StringBuilder();
        sb.AppendLine("请基于以下统计数据，生成【本月】时间使用总结。\n");
        sb.AppendLine($"【月份】{monthStart:yyyy年MM月}");
        sb.AppendLine($"【活跃天数】{activeDays}/{daysInMonth}");
        sb.AppendLine($"【总活跃时长】{TimeFormatHelper.Format(totalSeconds)}");
        sb.AppendLine($"【日均活跃时长】{TimeFormatHelper.Format(avg)}\n");

        sb.AppendLine("【分类时长】");
        sb.AppendLine(catLines);
        sb.AppendLine("\n【Top 15 应用】（务必完整列出全部 15 个，按时长降序；不足则列实际数量）");
        sb.AppendLine(topLines);
        sb.AppendLine("\n【每周对比】（按自然周，周一为起点）");
        sb.AppendLine(weekLines);

        sb.AppendLine("\n输出要求（使用 Markdown，结构完整，整体约 600~1000 字）：");
        sb.AppendLine("## 概览\n2~3 句话概括本月时间使用全貌与整体趋势。");
        sb.AppendLine("## 分类时长分析\n对各分类占比做月度解读，点明主要投入方向与长期失衡。");
        sb.AppendLine("## Top 15 应用亮点\n挑 3~5 个最具代表性的应用，说明其反映的工作/娱乐重心与变化。");
        sb.AppendLine("## 日均时长与活跃天数\n结合当月天数评价节奏是否健康、是否存在明显空缺或过载。");
        sb.AppendLine("## 周对比趋势\n基于每周对比，指出本月内使用强度的起伏与拐点。");
        sb.AppendLine("## 建议与改进\n给出 3 条左右具体、可执行的改进建议。");

        sb.AppendLine("\n注意：仅依据上述数据，不要虚构；数据有限处明确说明。");
        return sb.ToString();
    }
}
