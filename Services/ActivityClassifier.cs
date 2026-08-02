using System;
using System.Collections.Generic;
using System.Linq;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// 活动分类器 — 从数据库 Rules 表读取规则，给活动分类
/// Rules 表没有匹配到时，用内置默认规则兜底
/// </summary>
public class ActivityClassifier
{
    // 内存缓存：进程名 → 类别名
    private Dictionary<string, string> _processRules = new(StringComparer.OrdinalIgnoreCase);

    // 内存缓存：标题关键词 → 类别名
    private List<(string keyword, string category)> _titleKeywordRules = new();

    // 浏览器进程名（从数据库加载，分类为"网页"的规则进程名自动成为浏览器）
    private HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera"
    };

    // 内置默认规则（当 Rules 表为空时兜底）
    private static readonly Dictionary<string, string> DefaultProcessRules = new(StringComparer.OrdinalIgnoreCase)
    {
        { "devenv", "开发" }, { "Code", "开发" }, { "idea64", "开发" },
        { "pycharm64", "开发" }, { "cmd", "开发" }, { "powershell", "开发" },
        { "WindowsTerminal", "开发" }, { "git", "开发" },
        { "WeChat", "社交" }, { "QQ", "社交" }, { "Discord", "社交" }, { "Telegram", "社交" },
        { "Spotify", "娱乐" }, { "QQMusic", "娱乐" }, { "MusicPlayer2", "娱乐" },
        { "WINWORD", "学习" }, { "EXCEL", "学习" }, { "POWERPNT", "学习" },
        { "Acrobat", "学习" }, { "SumatraPDF", "学习" },
        { "explorer", "系统" }, { "SystemSettings", "系统" }, { "taskmgr", "系统" },
    };

    private static readonly List<(string, string)> DefaultTitleKeywordRules = new()
    {
        ("B站", "娱乐"), ("bilibili", "娱乐"), ("YouTube", "娱乐"),
        ("抖音", "娱乐"), ("斗鱼", "娱乐"), ("虎牙", "娱乐"), ("原神", "娱乐"),
        ("GitHub", "开发"), ("Stack Overflow", "开发"),
        ("CSDN", "学习"), ("知乎", "学习"), ("菜鸟教程", "学习"),
    };

    public ActivityClassifier()
    {
        ReloadRules();
    }

    /// <summary>
    /// 从数据库重新加载分类规则
    /// 设置页保存规则后调用此方法
    /// </summary>
    public void ReloadRules()
    {
        _processRules.Clear();
        _titleKeywordRules.Clear();

        try
        {
            var dbRules = DatabaseHelper.GetAllRules();
            var categories = DatabaseHelper.GetAllCategories();
            var catById = categories.ToDictionary(c => c.Id, c => c.Name);

            bool hasCustom = false;

            // 重置浏览器集合为默认，再从规则补充
            _browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "chrome", "msedge", "firefox", "brave", "opera" };

            // 找到"网页"分类的 ID
            var webCatId = catById.FirstOrDefault(c => c.Value == "网页").Key;

            foreach (var rule in dbRules)
            {
                if (!catById.TryGetValue(rule.CategoryId, out var catName))
                    continue;

                if (!string.IsNullOrWhiteSpace(rule.ProcessName))
                {
                    _processRules[rule.ProcessName] = catName;
                    hasCustom = true;

                    // 如果规则分类是"网页"，把进程加入浏览器集合
                    if (rule.CategoryId == webCatId)
                        _browsers.Add(rule.ProcessName);
                }

                if (!string.IsNullOrWhiteSpace(rule.TitleKeyword))
                {
                    _titleKeywordRules.Add((rule.TitleKeyword, catName));
                    hasCustom = true;
                }
            }

            // 如果数据库没有任何规则，用默认规则兜底
            if (!hasCustom)
            {
                foreach (var kv in DefaultProcessRules)
                    _processRules[kv.Key] = kv.Value;
                _titleKeywordRules.AddRange(DefaultTitleKeywordRules);
            }
            else
            {
                // 有自定义规则时也把默认规则合并进去（自定义优先）
                foreach (var kv in DefaultProcessRules)
                {
                    if (!_processRules.ContainsKey(kv.Key))
                        _processRules[kv.Key] = kv.Value;
                }
                // 默认标题关键词也合并
                foreach (var (kw, cat) in DefaultTitleKeywordRules)
                {
                    if (!_titleKeywordRules.Any(r => r.keyword.Equals(kw, StringComparison.OrdinalIgnoreCase)))
                        _titleKeywordRules.Add((kw, cat));
                }
            }
        }
        catch
        {
            // 数据库出错时用默认规则
            foreach (var kv in DefaultProcessRules)
                _processRules[kv.Key] = kv.Value;
            _titleKeywordRules.AddRange(DefaultTitleKeywordRules);
        }
    }

    /// <summary>
    /// 给一个活动分类
    /// </summary>
    public string Classify(string processName, string windowTitle)
    {
        if (string.IsNullOrEmpty(processName))
            return "未分类";

        // 1. 先按进程名精确匹配
        if (_processRules.TryGetValue(processName, out var category))
            return category;

        // 2. 浏览器特殊处理 — 按标题关键词分类
        if (_browsers.Contains(processName))
        {
            foreach (var (keyword, cat) in _titleKeywordRules)
            {
                if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }
            return "网页"; // 浏览器但没匹配到关键词
        }

        // 3. 按标题关键词兜底
        foreach (var (keyword, cat) in _titleKeywordRules)
        {
            if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return cat;
        }

        return "未分类";
    }
}
