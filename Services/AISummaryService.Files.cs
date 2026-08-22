// ============================================================================
// AISummaryService.Files.cs — AI 总结文件保存部分类
// 职责：把生成的总结 Markdown 按类型/日期写入本地文件夹（每次保留不覆盖），
//       并执行保留策略（最大份数/最大体积清理）。
// ============================================================================
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
    /// <summary>
    /// 保存 AI 总结到文件
    /// 直接存到 ai_summaries 根目录（不按日期建子文件夹），文件名带类型+日期范围+时分秒，每次保存都保留新文件不覆盖，受设置最大数量/大小控制
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
    /// 按设置清理旧的 AI 总结文件（扫描 ai_summaries 目录，兼容旧版本可能残留的子文件夹）
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
}
