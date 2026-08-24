// ============================================================================
// AISummaryService.cs — AI 总结服务（主体）：OpenAI 兼容端点调用与每日总结
// 提示词构建见 AISummaryService.Prompts.cs；总结文件保存见 AISummaryService.Files.cs。
// ============================================================================
// —— 命名空间导入：HTTP 客户端 / JSON 序列化 / 文本编码 / 项目内仓储与助手 ——
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
/// AI 每日总结服务 — 统一走 OpenAI 兼容接口（2026-08-23 起移除 Ollama 私有协议模式；
/// 本机 Ollama/LM Studio 通过其内置的 /v1 OpenAI 兼容端点同样适用）。
/// 提示词构建见 AISummaryService.Prompts.cs，总结文件保存见 AISummaryService.Files.cs（同属一个 partial class）。
/// </summary>
public partial class AISummaryService
{
    // 全局复用的 HTTP 客户端（默认超时；每次请求可用独立超时覆盖）
    // HttpClient 线程安全、全局单例复用，避免每次请求新建实例导致套接字耗尽
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    // AI 服务根地址，代码内部负责拼接为具体端点
    private string ApiUrl => SettingsRepository.Get("AIApiUrl", "");
    // API Key（OpenAI 兼容 Bearer 认证）
    private string ApiKey => SettingsRepository.Get("AIApiKey", "");
    // 模型名称
    private string AiModel => SettingsRepository.Get("AIModel", "");
    // AI 功能总开关
    private bool Enabled => SettingsRepository.Get("EnableAI", "true") == "true";

    // ==================== 端点推导与连通性 ====================

    /// <summary>由 Base URL 推导对话端点：尊重已写全的地址，否则补全 /v1/chat/completions。</summary>
    public static string BuildChatEndpoint(string baseUrl)
    {
        // 规整输入：去首尾空白与末尾斜杠
        var u = (baseUrl ?? "").Trim().TrimEnd('/');
        // 已经写全对话端点则原样返回
        if (u.EndsWith("/chat/completions")) return u;
        // 只写到 /v1 则补上 chat/completions 后缀
        if (u.EndsWith("/v1")) return u + "/chat/completions";
        // 裸地址则按标准 OpenAI 路径补全
        return u + "/v1/chat/completions";
    }

    /// <summary>由 Base URL 推导模型列表端点（GET，仅状态码不消耗 token）。</summary>
    public static string BuildModelsEndpoint(string baseUrl)
    {
        // 规整输入：去首尾空白与末尾斜杠
        var u = (baseUrl ?? "").Trim().TrimEnd('/');
        // 已写全 /models 则原样返回
        if (u.EndsWith("/models")) return u;
        // 写到 /v1 则只补 /models
        if (u.EndsWith("/v1")) return u + "/models";
        // 裸地址补全为 {base}/v1/models
        return u + "/v1/models";
    }

    /// <summary>
    /// 拉取模型列表（GET {base}/models）。供设置页"获取模型列表/测试连接"使用。
    /// </summary>
    /// <returns>Ok=HTTP 2xx；Status=状态码；Models=解析出的模型 id 列表；Error=异常消息</returns>
    public static async Task<(bool Ok, int? Status, List<string> Models, string Error)> TryFetchModelsAsync(
        string baseUrl, string apiKey, int timeoutSeconds = 10)
    {
        try
        {
            // 短生命周期客户端：仅本次探测使用；超时取传入秒数并强制下限 3 秒
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)) };
            // 构造 GET {base}/models 请求
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildModelsEndpoint(baseUrl));
            // 配置了 Key 才附加 Bearer 认证头
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
            // 发送请求（受上述超时约束）
            using var resp = await http.SendAsync(req);
            var status = (int)resp.StatusCode;
            // 非 2xx 直接判定失败，带回状态码与原因短语供 UI 展示
            if (!resp.IsSuccessStatusCode)
                return (false, status, new List<string>(), $"HTTP {status} {resp.ReasonPhrase}");
            // 读取响应体 JSON 文本
            var json = await resp.Content.ReadAsStringAsync();
            var models = new List<string>();
            // 按 OpenAI 风格解析：{"data":[{"id":"模型名"}, ...]}
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                // 逐个提取模型 id 收集到列表
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id)) models.Add(id.GetString() ?? "");
            }
            // 成功返回模型列表
            return (true, status, models, "");
        }
        catch (Exception ex)
        {
            // 网络/解析等一切异常统一转成 Error 消息，不向上抛
            return (false, null, new List<string>(), ex.Message);
        }
    }

    // ==================== 系统提示词 ====================

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

    // ==================== 每日总结 ====================

    /// <summary>
    /// 生成某一天的 AI 总结。从数据库拉当天活动数据，拼成 prompt 发给 AI，返回 Markdown 文本。
    /// </summary>
    /// <param name="date">要总结的日期</param>
    /// <returns>AI 生成的 Markdown 总结文本，失败或未启用时返回 null</returns>
    public async Task<string?> GenerateDailySummary(DateTime date)
    {
        // 没开 AI 功能直接返回
        if (!Enabled) return null;
        // 服务地址必填（统一 OpenAI 兼容端点）
        if (string.IsNullOrWhiteSpace(ApiUrl)) return null;

        // 拉当天的活动记录和统计汇总
        var activities = ActivityRepository.GetByDate(date);
        // 全天无活动记录：返回占位文案（调度器会把它当作成功结果入库）
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

    // ==================== HTTP 调用 ====================

    /// <summary>
    /// 调用 OpenAI 兼容接口（端点由 Base URL 拼接，见 BuildChatEndpoint）。
    /// 带 Bearer Token 认证，解析 choices[0].message.content。
    /// </summary>
    private async Task<string?> CallCustomAPI(string prompt)
    {
        // 地址未配置：记日志并快速失败
        if (string.IsNullOrWhiteSpace(ApiUrl))
        {
            Logger.Error("AI API Url 为空，请在设置中配置", null);
            return null;
        }

        // 构建请求体：模型名 + 消息。max_tokens / temperature 从设置读取，
        // 默认空值（不发送），由远端 API 使用自身默认值，避免写死 500 上限导致周/月报被截断。
        var requestBody = new Dictionary<string, object>
        {
            // 目标模型名
            ["model"] = AiModel,
            // 消息数组：system 固定角色纪律 + user 业务数据
            ["messages"] = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            },
            // 关闭流式输出：一次性取完整回复便于整体入库/存文件
            ["stream"] = false
        };
        // 可选参数：最大生成 token 数（未配置或非法则不发送该字段）
        var maxTokensRaw = SettingsRepository.Get("AIMaxTokens", "");
        if (!string.IsNullOrWhiteSpace(maxTokensRaw)
            && int.TryParse(maxTokensRaw, out var maxTokens) && maxTokens > 0)
            requestBody["max_tokens"] = maxTokens;
        // 可选参数：采样温度 0~2（未配置或非法则不发送）；用不变文化解析小数
        var tempRaw = SettingsRepository.Get("AITemperature", "");
        if (!string.IsNullOrWhiteSpace(tempRaw)
            && double.TryParse(tempRaw, System.Globalization.CultureInfo.InvariantCulture, out var temperature)
            && temperature >= 0 && temperature <= 2)
            requestBody["temperature"] = temperature;

        // 序列化为 JSON 字符串
        var json = JsonSerializer.Serialize(requestBody);
        // 端点由 Base URL 拼接：尊重已写全地址，否则补全 /v1/chat/completions
        var request = new HttpRequestMessage(HttpMethod.Post, BuildChatEndpoint(ApiUrl));
        // OpenAI 兼容格式用 Bearer Token 认证（本机服务留空则不带头）
        if (!string.IsNullOrWhiteSpace(ApiKey))
            request.Headers.Add("Authorization", $"Bearer {ApiKey}");
        // UTF-8 编码的 JSON 请求体
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // 请求级超时：设置里配了 AITimeoutSeconds 就用它，否则走客户端默认 120s
        HttpResponseMessage? response;
        using (var cts = new CancellationTokenSource())
        {
            // 读配置的超时秒数
            var timeoutRaw = SettingsRepository.Get("AITimeoutSeconds", "");
            // 仅接受 5~600 秒之间的合法配置，越界一律忽略
            if (int.TryParse(timeoutRaw, out var t) && t >= 5 && t <= 600)
                cts.CancelAfter(TimeSpan.FromSeconds(t));
            // 发送请求；超时会抛 TaskCanceledException，由 CallAIInternal 兜底记日志
            response = await _httpClient.SendAsync(request, cts.Token);
        }
        // 用 using 确保响应对象（及其内容流）一定被释放
        using var resp2 = response;
        if (!resp2.IsSuccessStatusCode)
        {
            // 4xx/5xx 时记录状态码与响应体，方便排查 AI 配置/鉴权问题，而非静默返回 null
            var errBody = await resp2.Content.ReadAsStringAsync();
            // 响应体截断到 500 字符，防止日志过大
            if (errBody.Length > 500) errBody = errBody.Substring(0, 500);
            Logger.Error($"自定义 AI API 返回错误：{(int)resp2.StatusCode} {resp2.ReasonPhrase}，响应体={errBody}", null);
            return null;
        }

        // 解析返回：choices 数组第一个元素的 message.content 就是回复文本
        var respJson = await resp2.Content.ReadAsStringAsync();
        // 解析响应 JSON；非法 JSON 抛异常，由上层 CallAIInternal 兜底
        using var doc = JsonDocument.Parse(respJson);

        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            // 取第一个 choice 的回复内容；若字段结构缺失会抛异常，同样由上层兜底
            return choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        // 没有 choices 字段：视为空回复
        return null;
    }

    /// <summary>
    /// 统一 AI 调用入口：全部走 OpenAI 兼容端点（由 Base URL 拼接）。
    /// 请求级超时优先读 AITimeoutSeconds 设置（未配置用客户端默认 120 秒）。
    /// </summary>
    private async Task<string?> CallAIInternal(string prompt)
    {
        try
        {
            // 目前仅一种后端实现（OpenAI 兼容），保留分发层以便扩展
            return await CallCustomAPI(prompt);
        }
        catch (Exception ex)
        {
            // 底层一切异常（网络/超时/JSON 结构不符）统一转为 null 并记日志，绝不向上抛
            Logger.Error("AI 总结生成失败", ex);
            return null;
        }
    }

    // ==================== Prompt 构建 ====================

    /// <summary>
    /// 拼接每日总结的 prompt：日期、类别时长、Top5 应用、输出格式要求。
    /// 采用带标签的结构化写法（参考 xiaohei-daily-backend），意图明确、便于模型遵循。
    /// </summary>
    private string BuildPrompt(DateTime date, Dictionary<string, int> catSummary, Dictionary<string, int> procSummary, int totalRecords)
    {
        // 各类别秒数求和即当日总活跃时长
        int totalSeconds = catSummary.Values.Sum();
        // 分类明细行：每行"类别名: 格式化时长"
        var catLines = string.Join("\n", catSummary.Select(c => $"  {c.Key}: {TimeFormatHelper.Format(c.Value)}"));
        // Top5 应用行：带序号、按时长降序（顺序依赖仓储返回的有序字典）
        var top = procSummary.Take(5)
            .Select((p, idx) => $"  {idx + 1}. {p.Key}: {TimeFormatHelper.Format(p.Value)}")
            .ToList();

        var sb = new StringBuilder();
        // —— 头部信息块：任务描述 + 基本信息 ——
        sb.AppendLine("请基于以下统计数据，生成【今日】时间使用总结。\n");
        sb.AppendLine($"【日期】{date:yyyy年MM月dd日}");
        sb.AppendLine($"【活动记录数】{totalRecords} 条");
        sb.AppendLine($"【总活跃时长】{TimeFormatHelper.Format(totalSeconds)}\n");

        // —— 数据块：分类时长与 Top 应用 ——
        sb.AppendLine("【分类时长】");
        sb.AppendLine(catLines);
        sb.AppendLine("\n【Top 5 应用】（务必完整列出全部 5 个，按时长降序；不足 5 个则列实际数量）");
        // 无应用记录时给出占位说明，避免空段让模型困惑
        sb.AppendLine(top.Count > 0 ? string.Join("\n", top) : "  （无应用记录）");

        // —— 输出要求块：章节结构与篇幅约束 ——
        sb.AppendLine("\n输出要求（使用 Markdown，篇幅精炼，整体约 200~350 字）：");
        sb.AppendLine("## 今日概况\n用 2~3 句话概括今天整体时间使用概况与最突出的特征。");
        sb.AppendLine("## 使用模式分析\n指出占比最高或异常的时间块、值得注意的趋势或失衡；若分布均衡可简要说明。");
        sb.AppendLine("## 建议\n给出 1 条具体、可立即执行的改进建议。");

        // —— 纪律性收尾：强调不编造、数据少时不硬凑 ——
        sb.AppendLine("\n注意：仅依据上述数据，不要虚构内容；若当天数据较少，概括即可，无需硬凑结论。");
        return sb.ToString();
    }
}
