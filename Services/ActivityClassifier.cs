using System;
using System.Collections.Generic;
using System.Linq;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// 活动分类器 — 从数据库 Rules 表读取规则，给活动分类
/// 预置规则 IsCustom=0（不可删），用户自定义规则 IsCustom=1
/// </summary>
public class ActivityClassifier
{
    // 内存缓存：进程名 → 类别名
    private Dictionary<string, string> _processRules = new(StringComparer.OrdinalIgnoreCase);

    // 内存缓存：标题关键词 → 类别名
    private List<(string keyword, string category)> _titleKeywordRules = new();

    // 浏览器进程名（分类为"网页"的进程自动成为浏览器）
    private HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera"
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
            var dbRules = RuleRepository.GetAll();
            var categories = CategoryRepository.GetAll();
            var catById = categories.ToDictionary(c => c.Id, c => c.Name);

            // 重置浏览器集合为默认，再从规则补充
            _browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "chrome", "msedge", "firefox", "brave", "opera" };

            // 找到"浏览器"分类的 ID
            var webCatId = catById.FirstOrDefault(c => c.Value == "浏览器").Key;

            foreach (var rule in dbRules)
            {
                if (!catById.TryGetValue(rule.CategoryId, out var catName))
                    continue;

                if (!string.IsNullOrWhiteSpace(rule.ProcessName))
                {
                    _processRules[rule.ProcessName] = catName;

                    // 如果规则分类是"浏览器"，把进程加入浏览器集合
                    if (rule.CategoryId == webCatId)
                        _browsers.Add(rule.ProcessName);
                }

                if (!string.IsNullOrWhiteSpace(rule.TitleKeyword))
                {
                    _titleKeywordRules.Add((rule.TitleKeyword, catName));
                }
            }
        }
        catch (Exception ex)
        {
            // 数据库出错时用最小兜底
            Logger.Error("分类器加载规则失败，用最小兜底", ex);
            _processRules["explorer"] = "系统组件";
            _processRules["chrome"] = "浏览器";
            _processRules["msedge"] = "浏览器";
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
            return "浏览器"; // 浏览器但没匹配到关键词
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
