using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using TimeActivity.Services;
using System.Text.Json;
using System.Threading.Tasks;
using TimeActivity.Data;
using TimeActivity.Models;

namespace TimeActivity.Services;

/// <summary>
/// AI 每日总结服务 — 支持两种模式：
/// 1. 局域网共享（Ollama）：本机 Ollama HTTP API，无需 Key
/// 2. 自定义 API：OpenAI 兼容格式，用户自填 URL/Key/Model
/// </summary>
public class AISummaryService
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private string ApiUrl => DatabaseHelper.GetSetting("AIApiUrl", "http://localhost:11434");
    private string ApiKey => DatabaseHelper.GetSetting("AIApiKey", "");
    private string AiModel => DatabaseHelper.GetSetting("AIModel", "qwen2.5:7b");
    private string AiMode => DatabaseHelper.GetSetting("AIMode", "lan");
    private bool Enabled => DatabaseHelper.GetSetting("EnableAI", "true") == "true";

    /// <summary>
    /// 生成某一天的 AI 总结
    /// </summary>
    public async Task<string?> GenerateDailySummary(DateTime date)
    {
        if (!Enabled) return null;
        if (AiMode == "lan" && string.IsNullOrEmpty(ApiUrl)) return null;
        if (AiMode == "custom" && string.IsNullOrEmpty(ApiKey)) return null;

        // 获取当天活动数据
        var activities = DatabaseHelper.GetActivitiesByDate(date);
        if (activities.Count == 0)
            return "当天没有活动记录。";

        // 获取类别统计
        var catSummary = DatabaseHelper.GetCategorySummaryByRange(date, date.AddDays(1));
        var procSummary = DatabaseHelper.GetProcessSummaryByRange(date, date.AddDays(1));

        string prompt = BuildPrompt(date, catSummary, procSummary, activities.Count);

        try
        {
            if (AiMode == "lan")
            {
                // Ollama 模式：POST http://localhost:11434/api/chat
                return await CallOllama(prompt);
            }
            else
            {
                // 自定义模式：OpenAI 兼容格式
                return await CallCustomAPI(prompt);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AI 总结生成失败", ex);
            return null;
        }
    }

    /// <summary>
    /// Ollama 模式 — 调用 /api/chat 接口
    /// </summary>
    private async Task<string?> CallOllama(string prompt)
    {
        var requestBody = new
        {
            model = AiModel,
            messages = new[]
            {
                new { role = "system", content = "你是一个时间管理助手。根据用户当天的电脑使用数据，生成简洁的每日总结。用中文回答，语气自然友好。使用 Markdown 格式输出，包含标题、列表、粗体等格式。3-5句话即可。" },
                new { role = "user", content = prompt }
            },
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody);
        var url = ApiUrl.TrimEnd('/') + "/api/chat";
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode) return null;

        var respJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);

        if (doc.RootElement.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var msgContent))
        {
            return msgContent.GetString();
        }
        return null;
    }

    /// <summary>
    /// 自定义模式 — OpenAI 兼容格式（MiniMax/OpenAI/DeepSeek 等）
    /// </summary>
    private async Task<string?> CallCustomAPI(string prompt)
    {
        var requestBody = new
        {
            model = AiModel,
            messages = new[]
            {
                new { role = "system", content = "你是一个时间管理助手。根据用户当天的电脑使用数据，生成简洁的每日总结。用中文回答，语气自然友好。使用 Markdown 格式输出，包含标题、列表、粗体等格式。3-5句话即可。" },
                new { role = "user", content = prompt }
            },
            max_tokens = 500,
            temperature = 0.7
        };

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("Authorization", $"Bearer {ApiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var respJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);

        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            return choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        return null;
    }

    private string BuildPrompt(DateTime date, Dictionary<string, int> catSummary, Dictionary<string, int> procSummary, int totalRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"日期：{date:yyyy年MM月dd日}");
        sb.AppendLine($"活动记录数：{totalRecords} 条");
        sb.AppendLine();

        sb.AppendLine("类别时长：");
        int totalSeconds = 0;
        foreach (var (cat, sec) in catSummary)
        {
            totalSeconds += sec;
            sb.AppendLine($"  {cat}：{FormatDuration(sec)}");
        }
        sb.AppendLine($"  总活跃时长：{FormatDuration(totalSeconds)}");
        sb.AppendLine();

        sb.AppendLine("Top 5 应用：");
        int rank = 1;
        foreach (var (proc, sec) in procSummary)
        {
            if (rank > 5) break;
            sb.AppendLine($"  {rank}. {proc}：{FormatDuration(sec)}");
            rank++;
        }

        sb.AppendLine();
        sb.AppendLine("请根据以上数据生成今日总结，使用 Markdown 格式，包括：");
        sb.AppendLine("- ## 今日概况（整体时间使用概况）");
        sb.AppendLine("- ## 使用模式分析（值得注意的占比、趋势）");
        sb.AppendLine("- ## 建议（一句简短建议）");

        return sb.ToString();
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}秒";
        long h = seconds / 3600;
        long m = (seconds % 3600) / 60;
        if (h > 0) return $"{h}小时{m}分钟";
        return $"{m}分钟";
    }

    // ========== 总结文件保存 ==========

    /// <summary>
    /// 保存 AI 总结到文件，文件名格式：AI_Summary_yyyy-MM-dd.txt
    /// 保存路径和存储限制从设置读取
    /// </summary>
    public static string? SaveSummaryToFile(string summary, DateTime date)
    {
        // 保存路径：设置里的 AISummaryPath，空则用程序目录下的 ai_summaries/
        string? configuredPath = DatabaseHelper.GetSetting("AISummaryPath", "");
        string dir = string.IsNullOrWhiteSpace(configuredPath)
            ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_summaries")
            : configuredPath;

        Directory.CreateDirectory(dir);

        string filename = $"summary_{date:yyyy-MM-dd}_{DateTime.Now:HHmmss}.md";
        string filepath = System.IO.Path.Combine(dir, filename);

        string content = $"# TimeActivity AI 每日总结\n\n**日期：{date:yyyy年MM月dd日}**  \n**生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}**\n\n---\n\n{summary}";

        File.WriteAllText(filepath, content, Encoding.UTF8);

        // 保存后执行存储限制清理
        CleanOldSummaries(dir);

        return filepath;
    }

    /// <summary>
    /// 按设置清理旧的 AI 总结文件
    /// </summary>
    private static void CleanOldSummaries(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "summary_*.md")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按数量限制
            if (int.TryParse(DatabaseHelper.GetSetting("AISummaryMaxCount", "0"), out int maxCount) && maxCount > 0)
            {
                while (files.Count > maxCount)
                {
                    files[0].Delete();
                    files.RemoveAt(0);
                }
            }

            // 按总大小限制（MB）
            if (int.TryParse(DatabaseHelper.GetSetting("AISummaryMaxSizeMB", "0"), out int maxSizeMB) && maxSizeMB > 0)
            {
                long maxBytes = maxSizeMB * 1024L * 1024L;
                long totalSize = files.Sum(f => f.Length);
                while (totalSize > maxBytes && files.Count > 0)
                {
                    totalSize -= files[0].Length;
                    files[0].Delete();
                    files.RemoveAt(0);
                }
            }
        }
        catch { }
    }

    // ========== 周/月总结 ==========

    /// <summary>
    /// 生成周总结
    /// </summary>
    public async Task<string?> GenerateWeeklySummary(DateTime weekStart)
    {
        if (!Enabled) return null;
        DateTime weekEnd = weekStart.AddDays(6);
        var catSummary = DatabaseHelper.GetCategorySummaryByRange(weekStart, weekEnd, false);
        var procSummary = DatabaseHelper.GetProcessSummaryByRange(weekStart, weekEnd, false);
        var dailyTotals = DatabaseHelper.GetDailyTotalsByRange(weekStart, weekEnd, false);

        string prompt = BuildWeeklyPrompt(weekStart, weekEnd, catSummary, procSummary, dailyTotals);
        return await CallAIInternal(prompt);
    }

    /// <summary>
    /// 生成月总结
    /// </summary>
    public async Task<string?> GenerateMonthlySummary(DateTime monthStart)
    {
        if (!Enabled) return null;
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var catSummary = DatabaseHelper.GetCategorySummaryByRange(monthStart, monthEnd, false);
        var procSummary = DatabaseHelper.GetProcessSummaryByRange(monthStart, monthEnd, false);
        var dailyTotals = DatabaseHelper.GetDailyTotalsByRange(monthStart, monthEnd, false);

        string prompt = BuildMonthlyPrompt(monthStart, monthEnd, catSummary, procSummary, dailyTotals);
        return await CallAIInternal(prompt);
    }

    /// <summary>
    /// 统一 AI 调用入口
    /// </summary>
    private async Task<string?> CallAIInternal(string prompt)
    {
        try
        {
            if (AiMode == "lan")
                return await CallOllama(prompt);
            else
                return await CallCustomAPI(prompt);
        }
        catch (Exception ex)
        {
            Logger.Error("AI 总结生成失败", ex);
            return null;
        }
    }

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
            sb.AppendLine($"- {c.Key}: {FormatDuration(c.Value)}");

        sb.AppendLine("\n### Top 10 应用");
        int i = 1;
        foreach (var p in procSummary.Take(10))
            sb.AppendLine($"{i++}. {p.Key}: {FormatDuration(p.Value)}");

        int activeDays = dailyTotals.Count(d => d.Value > 0);
        long totalSeconds = dailyTotals.Sum(d => (long)d.Value);
        sb.AppendLine($"\n### 活跃情况");
        sb.AppendLine($"- 活跃天数: {activeDays}/7");
        sb.AppendLine($"- 总活跃时长: {FormatDuration(totalSeconds)}");
        sb.AppendLine($"- 日均活跃: {FormatDuration(activeDays > 0 ? totalSeconds / activeDays : 0)}");

        sb.AppendLine("\n### 每日明细");
        foreach (var d in dailyTotals)
            sb.AppendLine($"- {d.Key}: {FormatDuration(d.Value)}");

        return sb.ToString();
    }

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
            sb.AppendLine($"- {c.Key}: {FormatDuration(c.Value)}");

        sb.AppendLine("\n### Top 15 应用");
        int i = 1;
        foreach (var p in procSummary.Take(15))
            sb.AppendLine($"{i++}. {p.Key}: {FormatDuration(p.Value)}");

        int activeDays = dailyTotals.Count(d => d.Value > 0);
        long totalSeconds = dailyTotals.Sum(d => (long)d.Value);
        int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        sb.AppendLine($"\n### 活跃情况");
        sb.AppendLine($"- 活跃天数: {activeDays}/{daysInMonth}");
        sb.AppendLine($"- 总活跃时长: {FormatDuration(totalSeconds)}");
        sb.AppendLine($"- 日均活跃: {FormatDuration(activeDays > 0 ? totalSeconds / activeDays : 0)}");

        // 周对比
        sb.AppendLine("\n### 每周对比");
        var weeklyGroups = dailyTotals
            .Select(d => { var dt = DateTime.Parse(d.Key); var weekNum = (dt.Day - 1) / 7 + 1; return (Week: weekNum, Seconds: d.Value); })
            .GroupBy(x => x.Week)
            .OrderBy(g => g.Key);
        foreach (var g in weeklyGroups)
        {
            long weekTotal = g.Sum(x => (long)x.Seconds);
            sb.AppendLine($"- 第{g.Key}周: {FormatDuration(weekTotal)}");
        }

        return sb.ToString();
    }
}
