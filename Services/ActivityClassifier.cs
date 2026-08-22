// ============================================================================
// ActivityClassifier.cs — 活动分类器
// 职责：启动时从 Rules 表加载全部规则到内存；Classify(进程名,标题) 按
//       "进程名精确匹配 + 标题关键词包含"的优先级返回分类名，未命中回退"未分类"。
// ReloadRules 在规则变更后重建内存缓存（线程安全性由调用方保证）。
// ============================================================================
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
    // 内存缓存：进程名 → 类别名（忽略大小写）。用 volatile 保证后台分类线程能看到最新替换
    private volatile Dictionary<string, string> _processRules = new(StringComparer.OrdinalIgnoreCase);

    // 内存缓存：标题关键词 → 类别名（列表，按顺序匹配）
    private volatile List<(string keyword, string category)> _titleKeywordRules = new();

    // 浏览器进程名集合（这些进程按标题关键词分类，而不是直接归为某个固定类别）
    private volatile HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase)
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
        // 先在本地构建完整集合，最后一次性换出字段引用（volatile 赋值原子可见），
        // 后台分类线程读到的永远是完整快照，不会命中"清空后、填充前"的半加载状态
        var processRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var titleKeywordRules = new List<(string keyword, string category)>();
        var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera"
        };

        try
        {
            var dbRules = RuleRepository.GetAll();
            var categories = CategoryRepository.GetAll();
            var catById = categories.ToDictionary(c => c.Id, c => c.Name);

            // 找到"浏览器"分类的 ID
            var webCatId = catById.FirstOrDefault(c => c.Value == "浏览器").Key;

            foreach (var rule in dbRules)
            {
                if (!catById.TryGetValue(rule.CategoryId, out var catName))
                    continue;

                if (!string.IsNullOrWhiteSpace(rule.ProcessName))
                {
                    processRules[rule.ProcessName] = catName;

                    // 如果规则分类是"浏览器"，把进程加入浏览器集合
                    if (rule.CategoryId == webCatId)
                        browsers.Add(rule.ProcessName);
                }

                if (!string.IsNullOrWhiteSpace(rule.TitleKeyword))
                {
                    titleKeywordRules.Add((rule.TitleKeyword, catName));
                }
            }
        }
        catch (Exception ex)
        {
            // 数据库出错时用最小兜底
            Logger.Error("分类器加载规则失败，用最小兜底", ex);
            processRules["explorer"] = "系统组件";
            processRules["chrome"] = "浏览器";
            processRules["msedge"] = "浏览器";
        }

        // 原子替换缓存
        _processRules = processRules;
        _titleKeywordRules = titleKeywordRules;
        _browsers = browsers;
    }

    /// <summary>
    /// 给一个活动分类。匹配优先级：进程名精确匹配 → 浏览器标题关键词 → 通用标题关键词 → 未分类。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="windowTitle">窗口标题</param>
    /// <returns>类别名称</returns>
    public string Classify(string processName, string windowTitle)
    {
        if (string.IsNullOrEmpty(processName))
            return "未分类";

        // 先抓一份字段快照，避免读取过程中字段被 ReloadRules 原子替换导致的不一致
        var processRules = _processRules;
        var titleKeywordRules = _titleKeywordRules;
        var browsers = _browsers;

        // 1. 先按进程名精确匹配（最快）
        if (processRules.TryGetValue(processName, out var category))
            return category;

        // 2. 浏览器特殊处理 — 按标题关键词分类（因为同一个浏览器可能在做不同的事）
        if (browsers.Contains(processName))
        {
            foreach (var (keyword, cat) in titleKeywordRules)
            {
                if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }
            return "浏览器"; // 浏览器但没匹配到关键词
        }

        // 3. 非浏览器按标题关键词兜底
        foreach (var (keyword, cat) in titleKeywordRules)
        {
            if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return cat;
        }

        return "未分类";
    }
}
