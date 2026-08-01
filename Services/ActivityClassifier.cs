using System;
using System.Collections.Generic;
using System.Linq;

namespace TimeActivity.Services;

/// <summary>
/// 活动分类器 — 根据进程名和窗口标题把活动归到某个类别
/// </summary>
public class ActivityClassifier
{
    // 预置规则：进程名 → 类别
    private static readonly Dictionary<string, string> ProcessRules = new()
    {
        // 开发
        { "devenv", "开发" },          // Visual Studio
        { "Code", "开发" },            // VS Code
        { "idea64", "开发" },          // IntelliJ IDEA
        { "pycharm64", "开发" },       // PyCharm
        { "cmd", "开发" },             // 命令行
        { "powershell", "开发" },      // PowerShell
        { "WindowsTerminal", "开发" }, // Windows Terminal
        { "git", "开发" },             // Git

        // 社交
        { "WeChat", "社交" },          // 微信
        { "QQ", "社交" },             // QQ
        { "Discord", "社交" },        // Discord
        { "Telegram", "社交" },       // Telegram

        // 娱乐
        { "Spotify", "娱乐" },        // Spotify
        { "QQMusic", "娱乐" },        // QQ音乐
        { "MusicPlayer2", "娱乐" },   // 本地音乐播放器

        // 学习/办公
        { "WINWORD", "学习" },        // Word
        { "EXCEL", "学习" },          // Excel
        { "POWERPNT", "学习" },       // PowerPoint
        { "Acrobat", "学习" },        // PDF阅读器
        { "SumatraPDF", "学习" },     // SumatraPDF

        // 系统
        { "explorer", "系统" },       // 资源管理器
        { "SystemSettings", "系统" }, // 设置
        { "taskmgr", "系统" },        // 任务管理器
    };

    // 标题关键词规则：标题包含关键词 → 类别
    private static readonly Dictionary<string, string> TitleKeywordRules = new()
    {
        { "B站", "娱乐" },
        { "bilibili", "娱乐" },
        { "YouTube", "娱乐" },
        { "抖音", "娱乐" },
        { "斗鱼", "娱乐" },
        { "虎牙", "娱乐" },
        { "原神", "娱乐" },
        { "GitHub", "开发" },
        { "Stack Overflow", "开发" },
        { "CSDN", "学习" },
        { "知乎", "学习" },
        { "菜鸟教程", "学习" },
    };

    // 浏览器进程名
    private static readonly HashSet<string> Browsers = new()
    {
        "chrome", "msedge", "firefox", "brave", "opera"
    };

    /// <summary>
    /// 给一个活动分类
    /// </summary>
    public string Classify(string processName, string windowTitle)
    {
        if (string.IsNullOrEmpty(processName))
            return "未分类";

        // 先按进程名精确匹配
        if (ProcessRules.TryGetValue(processName, out var category))
            return category;

        // 浏览器特殊处理 — 按标题关键词分类
        if (Browsers.Contains(processName.ToLower()))
        {
            foreach (var kv in TitleKeywordRules)
            {
                if (windowTitle.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            // 浏览器但没匹配到关键词 → 默认"网页"
            return "网页";
        }

        // 按标题关键词兜底
        foreach (var kv in TitleKeywordRules)
        {
            if (windowTitle.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return "未分类";
    }
}
