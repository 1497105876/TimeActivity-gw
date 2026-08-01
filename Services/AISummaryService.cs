using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
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
        catch
        {
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
                new { role = "system", content = "你是一个时间管理助手。根据用户当天的电脑使用数据，生成简洁的每日总结。用中文回答，语气自然友好，3-5句话即可。" },
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
                new { role = "system", content = "你是一个时间管理助手。根据用户当天的电脑使用数据，生成简洁的每日总结。用中文回答，语气自然友好，3-5句话即可。" },
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
        sb.AppendLine("请根据以上数据生成今日总结，包括：");
        sb.AppendLine("1. 今日整体时间使用概况");
        sb.AppendLine("2. 值得注意的使用模式（如某类活动占比特别高）");
        sb.AppendLine("3. 一句简短建议");

        return sb.ToString();
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}秒";
        int h = seconds / 3600;
        int m = (seconds % 3600) / 60;
        if (h > 0) return $"{h}小时{m}分钟";
        return $"{m}分钟";
    }
}
