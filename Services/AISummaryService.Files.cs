// ============================================================================
// AISummaryService.Files.cs — AI 总结文件保存部分类
// 职责：把生成的总结 Markdown 按类型/日期写入本地文件夹（每次保留不覆盖），
//       并执行保留策略（最大份数/最大体积清理）。
// ============================================================================
// —— 命名空间导入：基础类型 / 文件 IO / 文本编码 / 数据仓储 ——
using System;
using System.IO;
using System.Text;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// AISummaryService 的“总结文件保存”分部 — 负责把 AI 总结落盘与按设置清理旧文件。
/// 与 AISummaryService.cs 同属一个 partial class，公开 API 完全不变。
/// </summary>
public partial class AISummaryService
{
    // ==================== 落盘保存 ====================

    /// <summary>
    /// 保存 AI 总结到文件
    /// 直接存到 ai_summaries 根目录（不按日期建子文件夹），文件名带类型+日期范围+时分秒，每次保存都保留新文件不覆盖，受设置最大数量/大小控制
    /// </summary>
    public static string? SaveSummaryToFile(string summary, DateTime date, string summaryType = "daily")
    {
        // 保存路径：设置里的 AISummaryPath，空则用程序目录下的 ai_summaries/
        string? configuredPath = SettingsRepository.Get("AISummaryPath", "");
        // 未配置/空白路径时回退默认目录；配置了就用用户目录
        string baseDir = string.IsNullOrWhiteSpace(configuredPath)
            ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_summaries")
            : configuredPath;

        // 直接存到根目录，不再按日期建子文件夹
        // 确保目录存在（不存在则连同多级父目录一起创建）
        Directory.CreateDirectory(baseDir);

        // 文件名带日期范围+时分秒
        // 按类型生成日期段：周报给"起~止"区间、月报只到月份、日报给完整日期
        string datePart = summaryType switch
        {
            // 周报：周一 +6 天得到周日，拼成 MM-dd_to_MM-dd
            "weekly" => $"{date:MM-dd}_to_{date.AddDays(6):MM-dd}",
            // 月报：仅年月
            "monthly" => $"{date:MM}",
            // 默认（daily）：完整年月日
            _ => $"{date:yyyy-MM-dd}"
        };
        // 秒级时间戳保证多次保存互不覆盖；注意同一秒内重复保存仍会同名覆盖
        string filename = $"summary_{summaryType}_{datePart}_{DateTime.Now:HHmmss}.md";
        string filepath = System.IO.Path.Combine(baseDir, filename);

        // 类型中文标签，用于文档大标题
        string typeLabel = summaryType switch
        {
            "weekly" => "每周总结",
            "monthly" => "每月总结",
            _ => "每日总结"
        };
        // 组装 Markdown 内容：标题 + 元信息（日期/生成时间）+ 分隔线 + AI 正文
        string content = $"# TimeActivity AI {typeLabel}\n\n**日期：{date:yyyy年MM月dd日}**  \n**生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}**\n\n---\n\n{summary}";

        // 以 UTF-8 写盘；IO 异常（权限/占用）不在此吞掉，会抛给调用方
        File.WriteAllText(filepath, content, Encoding.UTF8);

        // 保存后执行存储限制清理（扫整个 baseDir）
        CleanOldSummaries(baseDir);

        // 返回落盘路径供调用方展示/记录
        return filepath;
    }

    // ==================== 保留策略清理 ====================

    /// <summary>
    /// 按设置清理旧的 AI 总结文件（扫描 ai_summaries 目录，兼容旧版本可能残留的子文件夹）
    /// </summary>
    private static void CleanOldSummaries(string baseDir)
    {
        try
        {
            // 递归收集所有 summary_*.md（含旧版本子文件夹中的遗留），按创建时间从老到新排序
            var files = Directory.GetFiles(baseDir, "summary_*.md", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按数量限制
            if (int.TryParse(SettingsRepository.Get("AISummaryMaxCount", "0"), out int maxCount) && maxCount > 0)
            {
                // 超出上限就从最老的开始删，直到数量达标
                while (files.Count > maxCount)
                {
                    // 注意：Delete 无单独容错——单个文件被占用/已消失会让整个清理中断（外层 catch 兜底）
                    files[0].Delete();
                    files.RemoveAt(0);
                }
            }

            // 按总大小限制（MB）
            if (int.TryParse(SettingsRepository.Get("AISummaryMaxSizeMB", "0"), out int maxSizeMB) && maxSizeMB > 0)
            {
                // 上限换算为字节（乘数用 long 防止 int 溢出）
                long maxBytes = maxSizeMB * 1024L * 1024L;
                // 当前留存文件总字节数
                long totalSize = files.Sum(f => f.Length);
                // 超限则从最老的开始删，先扣减累计大小再移除，保证循环收敛
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
            // 清理属于锦上添花：任何失败只记日志，绝不影响刚保存成功的总结
            Logger.Error("AI 总结文件清理失败", ex);
        }
    }
}
