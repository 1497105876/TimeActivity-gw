using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using TimeActivity.Services;
using TimeActivity.Helpers;
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
    // 全局复用的 HTTP 客户端，超时 120 秒（AI 模型生成可能比较慢）
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    // AI 服务地址，Ollama 默认本机 11434 端口
    private string ApiUrl => SettingsRepository.Get("AIApiUrl", "http://localhost:11434");
    // API Key，自定义模式才需要
    private string ApiKey => SettingsRepository.Get("AIApiKey", "");
    // 模型名称，默认用 qwen2.5:7b
    private string AiModel => SettingsRepository.Get("AIModel", "qwen2.5:7b");
    // 模式：lan=局域网 Ollama，custom=自定义 OpenAI 兼容 API
    private string AiMode => SettingsRepository.Get("AIMode", "lan");
    // AI 功能总开关
    private bool Enabled => SettingsRepository.Get("EnableAI", "true") == "true";

    /// <summary>
    /// 生成某一天的 AI 总结。从数据库拉当天活动数据，拼成 prompt 发给 AI，返回 Markdown 文本。
    /// </summary>
    /// <param name="date">要总结的日期</param>
    /// <returns>AI 生成的 Markdown 总结文本，失败或未启用时返回 null</returns>
    public async Task<string?> GenerateDailySummary(DateTime date)
    {
        // 没开 AI 功能直接返回
        if (!Enabled) return null;
        // Ollama 模式只需要 URL，自定义模式必须有 API Key
        if (AiMode == "lan" && string.IsNullOrEmpty(ApiUrl)) return null;
        if (AiMode == "custom" && string.IsNullOrEmpty(ApiKey)) return null;

        // 拉当天的活动记录和统计汇总
        var activities = ActivityRepository.GetByDate(date);
        if (activities.Count == 0)
            return "当天没有活动记录。";

        // 类别时长统计 + 进程时长统计
        var catSummary = ActivityRepository.GetCategorySummaryByRange(date, date.AddDays(1));
        var procSummary = ActivityRepository.GetProcessSummaryByRange(date, date.AddDays(1));

        // 拼接发给 AI 的 prompt
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
    /// Ollama 模式 — 调用本地 Ollama 的 /api/chat 接口获取 AI 回复。
    /// 调用前会先检测 Ollama 服务是否在线。
    /// </summary>
    /// <param name="prompt">拼好的用户提示词</param>
    /// <returns>AI 回复的文本，失败返回 null</returns>
    private async Task<string?> CallOllama(string prompt)
    {
        if (string.IsNullOrWhiteSpace(ApiUrl))
        {
            Logger.Error("Ollama API Url 为空，请在设置中配置", null);
            return null;
        }

        // 先检测 Ollama 是否在线（请求 /api/tags 列出本地模型）
        try
        {
            using var checkResp = await _httpClient.GetAsync(ApiUrl.TrimEnd('/') + "/api/tags");
            if (!checkResp.IsSuccessStatusCode)
            {
                Logger.Error($"Ollama 服务返回错误状态 {checkResp.StatusCode}，请确认 Ollama 正在运行", null);
                return null;
            }
        }
        catch (HttpRequestException)
        {
            Logger.Error($"无法连接到 Ollama 服务（{ApiUrl}），请确认 Ollama 已启动", null);
            return null;
        }

        // 构建请求体：模型名 + system/user 消息 + 关闭流式输出
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
        using var resp = response;
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            Logger.Error($"Ollama /api/chat 返回 {resp.StatusCode}，模型={AiModel}，响应={errBody.Substring(0, Math.Min(500, errBody.Length))}", null);
            return null;
        }

        // 解析 Ollama 返回的 JSON：message.content 字段就是回复文本
        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);

        if (doc.RootElement.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var msgContent))
        {
            return msgContent.GetString();
        }
        Logger.Error($"Ollama 返回 JSON 无 message.content 字段，响应={respJson.Substring(0, Math.Min(500, respJson.Length))}", null);
        return null;
    }

    /// <summary>
    /// 自定义模式 — 调用 OpenAI 兼容格式的 API（MiniMax/OpenAI/DeepSeek 等）。
    /// 带 Bearer Token 认证，解析 choices[0].message.content。
    /// </summary>
    /// <param name="prompt">拼好的用户提示词</param>
    /// <returns>AI 回复的文本，失败返回 null</returns>
    private async Task<string?> CallCustomAPI(string prompt)
    {
        if (string.IsNullOrWhiteSpace(ApiUrl))
        {
            Logger.Error("自定义 AI API Url 为空，请在设置中配置", null);
            return null;
        }

        // 构建请求体：模型名 + 消息 + 最大 token 数 + 温度
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
        // OpenAI 兼容格式用 Bearer Token 认证
        request.Headers.Add("Authorization", $"Bearer {ApiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        using var resp2 = response;
        if (!resp2.IsSuccessStatusCode) return null;

        // 解析返回：choices 数组第一个元素的 message.content 就是回复文本
        var respJson = await resp2.Content.ReadAsStringAsync();
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

    /// <summary>
    /// 拼接每日总结的 prompt：日期、类别时长、Top5 应用、输出格式要求。
    /// </summary>
    /// <param name="date">日期</param>
    /// <param name="catSummary">类别→秒数 的统计字典</param>
    /// <param name="procSummary">进程名→秒数 的统计字典</param>
    /// <param name="totalRecords">当天活动记录总数</param>
    /// <returns>拼好的 prompt 字符串</returns>
    private string BuildPrompt(DateTime date, Dictionary<string, int> catSummary, Dictionary<string, int> procSummary, int totalRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"日期：{date:yyyy年MM月dd日}");
        sb.AppendLine($"活动记录数：{totalRecords} 条");
        sb.AppendLine();

        // 类别时长汇总，顺便算总活跃时间
        sb.AppendLine("类别时长：");
        int totalSeconds = 0;
        foreach (var (cat, sec) in catSummary)
        {
            totalSeconds += sec;
            sb.AppendLine($"  {cat}：{TimeFormatHelper.Format(sec)}");
        }
        sb.AppendLine($"  总活跃时长：{TimeFormatHelper.Format(totalSeconds)}");
        sb.AppendLine();

        // Top 5 应用
        sb.AppendLine("Top 5 应用：");
        int rank = 1;
        foreach (var (proc, sec) in procSummary)
        {
            if (rank > 5) break;
            sb.AppendLine($"  {rank}. {proc}：{TimeFormatHelper.Format(sec)}");
            rank++;
        }

        // 告诉 AI 输出格式
        sb.AppendLine();
        sb.AppendLine("请根据以上数据生成今日总结，使用 Markdown 格式，包括：");
        sb.AppendLine("- ## 今日概况（整体时间使用概况）");
        sb.AppendLine("- ## 使用模式分析（值得注意的占比、趋势）");
        sb.AppendLine("- ## 建议（一句简短建议）");

        return sb.ToString();
    }

    // FormatDuration 已移到 TimeFormatHelper

    // ========== 总结文件保存 ==========

    /// <summary>
    /// 保存 AI 总结到文件
    /// 按日期文件夹分：如 2026-08-02/summary_daily_143025.md
    /// 每次保存都保留新文件不覆盖，受设置最大数量/大小控制
    /// </summary>
    public static string? SaveSummaryToFile(string summary, DateTime date, string summaryType = "daily")
    {
        // 保存路径：设置里的 AISummaryPath，空则用程序目录下的 ai_summaries/
        string? configuredPath = SettingsRepository.Get("AISummaryPath", "");
        string baseDir = string.IsNullOrWhiteSpace(configuredPath)
            ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_summaries")
            : configuredPath;

        // 直接存到根目录，不再按日期建子文件夹
        Directory.CreateDirectory(baseDir);

        // 文件名带日期范围+时分秒
        string datePart = summaryType switch
        {
            "weekly" => $"{date:MM-dd}_to_{date.AddDays(6):MM-dd}",
            "monthly" => $"{date:MM}",
            _ => $"{date:yyyy-MM-dd}"
        };
        string filename = $"summary_{summaryType}_{datePart}_{DateTime.Now:HHmmss}.md";
        string filepath = System.IO.Path.Combine(baseDir, filename);

        string typeLabel = summaryType switch
        {
            "weekly" => "每周总结",
            "monthly" => "每月总结",
            _ => "每日总结"
        };
        string content = $"# TimeActivity AI {typeLabel}\n\n**日期：{date:yyyy年MM月dd日}**  \n**生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}**\n\n---\n\n{summary}";

        File.WriteAllText(filepath, content, Encoding.UTF8);

        // 保存后执行存储限制清理（扫整个 baseDir）
        CleanOldSummaries(baseDir);

        return filepath;
    }

    /// <summary>
    /// 按设置清理旧的 AI 总结文件（递归扫描含子文件夹）
    /// </summary>
    private static void CleanOldSummaries(string baseDir)
    {
        try
        {
            var files = Directory.GetFiles(baseDir, "summary_*.md", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按数量限制
            if (int.TryParse(SettingsRepository.Get("AISummaryMaxCount", "0"), out int maxCount) && maxCount > 0)
            {
                while (files.Count > maxCount)
                {
                    files[0].Delete();
                    files.RemoveAt(0);
                }
            }

            // 按总大小限制（MB）
            if (int.TryParse(SettingsRepository.Get("AISummaryMaxSizeMB", "0"), out int maxSizeMB) && maxSizeMB > 0)
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
        catch (Exception ex)
        {
            Logger.Error("AI 总结文件清理失败", ex);
        }
    }

    // ========== 周/月总结 ==========

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
    /// 统一 AI 调用入口，根据当前模式分发到 Ollama 或自定义 API。
    /// </summary>
    /// <param name="prompt">拼好的提示词</param>
    /// <returns>AI 回复文本，失败返回 null</returns>
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

    /// <summary>
    /// 拼接周总结 prompt：包含分类时长、Top 10 应用、活跃天数、每日明细。
    /// </summary>
    /// <param name="weekStart">周一日期</param>
    /// <param name="weekEnd">周日日期</param>
    /// <param name="catSummary">类别→秒数</param>
    /// <param name="procSummary">进程名→秒数</param>
    /// <param name="dailyTotals">日期字符串→秒数</param>
    /// <returns>拼好的 prompt</returns>
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
    /// <param name="monthStart">月初日期</param>
    /// <param name="monthEnd">月末日期</param>
    /// <param name="catSummary">类别→秒数</param>
    /// <param name="procSummary">进程名→秒数</param>
    /// <param name="dailyTotals">日期字符串→秒数</param>
    /// <returns>拼好的 prompt</returns>
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
