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
    /// 只定义角色、语言、格式与"不编造 / 直接输出"等纪律，篇幅由日/周/月各自的用户 prompt 控制，
    /// 避免把"3-5 句话"这类写死长度与周/月报的多章节要求打架（参考 xiaohei-daily-backend 的写法）。
    /// </summary>
    private const string SystemPrompt = @"你是一位时间管理分析助手，专门根据用户的电脑使用时长统计数据，生成日 / 周 / 月时间使用总结。

请严格遵守以下要求：
1. 使用中文，语气自然、友好、客观，像一位贴心的效率教练，不要使用生硬的公文腔。
2. 严格基于用户提供的统计数据作答，不得编造数据中不存在的应用、时长或结论；当数据不足以支撑某条结论时，明确说明""数据有限""，不要臆测。
3. 使用 Markdown 格式输出，合理使用二级标题（##）、列表、粗体、表格等，层次清晰。
4. 直接输出总结正文；不要添加""以下是……"" ""根据您提供的数据""之类开场白，也不要在结尾追加与总结无关的元评论。
5. 分析与建议要结合时间管理常识，给出具体、可执行的改进方向，避免空泛套话（如""请合理安排时间""）。";

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

        // 类别时长统计 + 进程时长统计（用单日方法，避免 Range 含右边界把次日数据算进当天）
        var catSummary = ActivityRepository.GetCategorySummaryByDate(date);
        var procSummary = ActivityRepository.GetProcessSummaryByDate(date);

        // 拼接发给 AI 的 prompt
        string prompt = BuildPrompt(date, catSummary, procSummary, activities.Count);

        // 日报与周/月报统一走同一分发入口，避免两份同构逻辑（改分发时漏改一处）
        return await CallAIInternal(prompt);
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

        // 构建请求体：模型名 + 消息。max_tokens / temperature 从设置读取，
        // 默认空值（不发送），由远端 API 使用自身默认值，避免写死 500 上限导致周/月报被截断。
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = AiModel,
            ["messages"] = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            },
            ["stream"] = false
        };
        var maxTokensRaw = SettingsRepository.Get("AIMaxTokens", "");
        if (!string.IsNullOrWhiteSpace(maxTokensRaw)
            && int.TryParse(maxTokensRaw, out var maxTokens) && maxTokens > 0)
            requestBody["max_tokens"] = maxTokens;
        var tempRaw = SettingsRepository.Get("AITemperature", "");
        if (!string.IsNullOrWhiteSpace(tempRaw)
            && double.TryParse(tempRaw, System.Globalization.CultureInfo.InvariantCulture, out var temperature)
            && temperature >= 0 && temperature <= 2)
            requestBody["temperature"] = temperature;

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        // OpenAI 兼容格式用 Bearer Token 认证
        request.Headers.Add("Authorization", $"Bearer {ApiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        using var resp2 = response;
        if (!resp2.IsSuccessStatusCode)
        {
            // 4xx/5xx 时记录状态码与响应体，方便排查 AI 配置/鉴权问题，而非静默返回 null
            var errBody = await resp2.Content.ReadAsStringAsync();
            if (errBody.Length > 500) errBody = errBody.Substring(0, 500);
            Logger.Error($"自定义 AI API 返回错误：{(int)resp2.StatusCode} {resp2.ReasonPhrase}，响应体={errBody}", null);
            return null;
        }

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
    /// 采用带标签的结构化写法（参考 xiaohei-daily-backend），意图明确、便于模型遵循。
    /// </summary>
    private string BuildPrompt(DateTime date, Dictionary<string, int> catSummary, Dictionary<string, int> procSummary, int totalRecords)
    {
        int totalSeconds = catSummary.Values.Sum();
        var catLines = string.Join("\n", catSummary.Select(c => $"  {c.Key}: {TimeFormatHelper.Format(c.Value)}"));
        var top = procSummary.Take(5)
            .Select((p, idx) => $"  {idx + 1}. {p.Key}: {TimeFormatHelper.Format(p.Value)}")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("请基于以下统计数据，生成【今日】时间使用总结。\n");
        sb.AppendLine($"【日期】{date:yyyy年MM月dd日}");
        sb.AppendLine($"【活动记录数】{totalRecords} 条");
        sb.AppendLine($"【总活跃时长】{TimeFormatHelper.Format(totalSeconds)}\n");

        sb.AppendLine("【分类时长】");
        sb.AppendLine(catLines);
        sb.AppendLine("\n【Top 5 应用】（务必完整列出全部 5 个，按时长降序；不足 5 个则列实际数量）");
        sb.AppendLine(top.Count > 0 ? string.Join("\n", top) : "  （无应用记录）");

        sb.AppendLine("\n输出要求（使用 Markdown，篇幅精炼，整体约 200~350 字）：");
        sb.AppendLine("## 今日概况\n用 2~3 句话概括今天整体时间使用概况与最突出的特征。");
        sb.AppendLine("## 使用模式分析\n指出占比最高或异常的时间块、值得注意的趋势或失衡；若分布均衡可简要说明。");
        sb.AppendLine("## 建议\n给出 1 条具体、可立即执行的改进建议。");

        sb.AppendLine("\n注意：仅依据上述数据，不要虚构内容；若当天数据较少，概括即可，无需硬凑结论。");
        return sb.ToString();
    }
}
