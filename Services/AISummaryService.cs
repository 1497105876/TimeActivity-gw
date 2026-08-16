using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TimeActivity.Data;
using TimeActivity.Helpers;
using TimeActivity.Models;

namespace TimeActivity.Services;

/// <summary>
/// AI 每日总结服务 — 支持两种模式：
/// 1. 局域网共享（Ollama）：本机 Ollama HTTP API，无需 Key
/// 2. 自定义 API：OpenAI 兼容格式，用户自填 URL/Key/Model
/// 提示词构建见 AISummaryService.Prompts.cs，总结文件保存见 AISummaryService.Files.cs（同属一个 partial class）。
/// </summary>
public partial class AISummaryService
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
    /// 两类 AI 模式共用的系统提示词（拼接 prompt 时作为 system 消息内容）。
    /// </summary>
    private const string SystemPrompt = "你是一个时间管理助手。根据用户当天的电脑使用数据，生成简洁的每日总结。用中文回答，语气自然友好。使用 Markdown 格式输出，包含标题、列表、粗体等格式。3-5句话即可。";

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
                new { role = "system", content = SystemPrompt },
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
                new { role = "system", content = SystemPrompt },
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
}
