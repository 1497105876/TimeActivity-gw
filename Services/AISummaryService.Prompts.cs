using System;
using System.Collections.Generic;
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
        if (!Enabled) return null;
        DateTime weekEnd = weekStart.AddDays(6);
        var catSummary = ActivityRepository.GetCategorySummaryByRange(weekStart, weekEnd, false);
        var procSummary = ActivityRepository.GetProcessSummaryByRange(weekStart, weekEnd, false);
        var dailyTotals = ActivityRepository.GetDailyTotalsByRange(weekStart, weekEnd, false);

        int totalSeconds = catSummary.Values.Sum();
        if (totalSeconds == 0)
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
        if (!Enabled) return null;
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var catSummary = ActivityRepository.GetCategorySummaryByRange(monthStart, monthEnd, false);
        var procSummary = ActivityRepository.GetProcessSummaryByRange(monthStart, monthEnd, false);
        var dailyTotals = ActivityRepository.GetDailyTotalsByRange(monthStart, monthEnd, false);

        int totalSeconds = catSummary.Values.Sum();
        if (totalSeconds == 0)
            return "本月没有活动记录。";

        string prompt = BuildMonthlyPrompt(monthStart, monthEnd, catSummary, procSummary, dailyTotals);
        return await CallAIInternal(prompt);
    }

    /// <summary>
    /// 拼接周总结 prompt：包含分类时长、Top 10 应用、活跃天数、每日明细。
    /// </summary>
    private string BuildWeeklyPrompt(DateTime weekStart, DateTime weekEnd,
        Dictionary<string, int> catSummary, Dictionary<string, int> procSummary,
        Dictionary<string, int> dailyTotals)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 本周时间使用总结（{weekStart:MM-dd} ~ {weekEnd:MM-dd}）\n");
        sb.AppendLine("请用 Markdown 格式输出本周时间使用总结，包含以下几个部分：");
        sb.AppendLine("## 概览");
        sb.AppendLine("## 分类时长分析");
        sb.AppendLine("## Top 10 应用");
        sb.AppendLine("## 日均时长与活跃天数");
        sb.AppendLine("## 建议与改进\n");
        sb.AppendLine("以下是本周的数据：\n");

        sb.AppendLine("### 分类时长");
        foreach (var c in catSummary)
            sb.AppendLine($"- {c.Key}: {TimeFormatHelper.Format(c.Value)}");

        sb.AppendLine("\n### Top 10 应用");
        sb.AppendLine("请务必列出以下全部 10 个应用，不要省略：");
        int i = 1;
        foreach (var p in procSummary.Take(10))
            sb.AppendLine($"{i++}. {p.Key}: {TimeFormatHelper.Format(p.Value)}");

        // 计算活跃天数和日均时长
        int activeDays = dailyTotals.Count(d => d.Value > 0);
        long totalSeconds = dailyTotals.Sum(d => (long)d.Value);
        sb.AppendLine($"\n### 活跃情况");
        sb.AppendLine($"- 活跃天数: {activeDays}/7");
        sb.AppendLine($"- 总活跃时长: {TimeFormatHelper.Format(totalSeconds)}");
        sb.AppendLine($"- 日均活跃: {TimeFormatHelper.Format(activeDays > 0 ? totalSeconds / activeDays : 0)}");

        // 每日明细
        sb.AppendLine("\n### 每日明细");
        foreach (var d in dailyTotals)
            sb.AppendLine($"- {d.Key}: {TimeFormatHelper.Format(d.Value)}");

        return sb.ToString();
    }

    /// <summary>
    /// 拼接月总结 prompt：包含分类时长、Top 15 应用、活跃天数、每周对比。
    /// </summary>
    private string BuildMonthlyPrompt(DateTime monthStart, DateTime monthEnd,
        Dictionary<string, int> catSummary, Dictionary<string, int> procSummary,
        Dictionary<string, int> dailyTotals)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 本月时间使用总结（{monthStart:yyyy-MM}）\n");
        sb.AppendLine("请用 Markdown 格式输出本月时间使用总结，包含以下几个部分：");
        sb.AppendLine("## 概览");
        sb.AppendLine("## 分类时长分析");
        sb.AppendLine("## Top 15 应用");
        sb.AppendLine("## 日均时长与活跃天数");
        sb.AppendLine("## 周对比");
        sb.AppendLine("## 建议与改进\n");
        sb.AppendLine("以下是本月的数据：\n");

        sb.AppendLine("### 分类时长");
        foreach (var c in catSummary)
            sb.AppendLine($"- {c.Key}: {TimeFormatHelper.Format(c.Value)}");

        sb.AppendLine("\n### Top 15 应用");
        sb.AppendLine("请务必列出以下全部 15 个应用，不要省略：");
        int i = 1;
        foreach (var p in procSummary.Take(15))
            sb.AppendLine($"{i++}. {p.Key}: {TimeFormatHelper.Format(p.Value)}");

        // 计算月内活跃天数和日均
        int activeDays = dailyTotals.Count(d => d.Value > 0);
        long totalSeconds = dailyTotals.Sum(d => (long)d.Value);
        int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        sb.AppendLine($"\n### 活跃情况");
        sb.AppendLine($"- 活跃天数: {activeDays}/{daysInMonth}");
        sb.AppendLine($"- 总活跃时长: {TimeFormatHelper.Format(totalSeconds)}");
        sb.AppendLine($"- 日均活跃: {TimeFormatHelper.Format(activeDays > 0 ? totalSeconds / activeDays : 0)}");

        // 按周分组对比（每月按 7 天分 4~5 周）
        sb.AppendLine("\n### 每周对比");
        var weeklyGroups = dailyTotals
            .Select(d => { var dt = DateTime.Parse(d.Key); var weekNum = (dt.Day - 1) / 7 + 1; return (Week: weekNum, Seconds: d.Value); })
            .GroupBy(x => x.Week)
            .OrderBy(g => g.Key);
        foreach (var g in weeklyGroups)
        {
            long weekTotal = g.Sum(x => (long)x.Seconds);
            sb.AppendLine($"- 第{g.Key}周: {TimeFormatHelper.Format(weekTotal)}");
        }

        return sb.ToString();
    }
}
