// ============================================================================
// ActivityClassifier.cs — 活动分类器
// 职责：启动时从 Rules 表加载全部规则到内存；Classify(进程名,标题) 按
//       "进程名精确匹配 + 标题关键词包含"的优先级返回分类名，未命中回退"未分类"。
// ReloadRules 在规则变更后重建内存缓存（线程安全性由调用方保证）。
// ============================================================================
// —— 命名空间导入：基础类型 / 泛型集合与 LINQ / 数据仓储层 ——
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
    // volatile 只保证引用替换的可见性与有序性；字典本身建成后不再修改，因此并发只读安全
    private volatile Dictionary<string, string> _processRules = new(StringComparer.OrdinalIgnoreCase);

    // 内存缓存：标题关键词 → 类别名（列表，按顺序匹配）
    // 同样以"整体替换"方式更新；元素为元组，只读遍历安全
    private volatile List<(string keyword, string category)> _titleKeywordRules = new();

    // 浏览器进程名集合（这些进程按标题关键词分类，而不是直接归为某个固定类别）
    // 内置五大主流浏览器；规则表中归类为"浏览器"的自定义进程会在 ReloadRules 时补充进来
    private volatile HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera"
    };

    /// <summary>
    /// 构造函数：创建实例时立即从数据库加载一次规则，保证分类器开箱可用。
    /// </summary>
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

        // 本地构建 1/3：进程名 → 类别名 的精确匹配表
        var processRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 本地构建 2/3：标题关键词规则列表（保留数据库返回顺序，匹配时按序首个命中生效）
        var titleKeywordRules = new List<(string keyword, string category)>();
        // 本地构建 3/3：浏览器进程集合，先放入内置的五个主流浏览器进程名
        var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera"
        };

        // 整个加载过程包在 try 里：数据库损坏/连接失败时不让构造或设置页保存崩溃，
        // 而是降级到 catch 里的最小兜底规则
        try
        {
            // 拉取全量规则与全量类别
            var dbRules = RuleRepository.GetAll();
            var categories = CategoryRepository.GetAll();
            // 建立 类别Id → 类别名 字典，后续把规则的 CategoryId 翻译成可读类别名
            var catById = categories.ToDictionary(c => c.Id, c => c.Name);

            // 找到"浏览器"分类的 ID
            // 注意：若类别表里不存在"浏览器"，FirstOrDefault 返回默认键值对，Key 为 0
            var webCatId = catById.FirstOrDefault(c => c.Value == "浏览器").Key;

            // 逐条处理数据库规则：翻译类别名并分发到对应的本地集合
            foreach (var rule in dbRules)
            {
                // 规则指向的类别已不存在（被删除）时跳过，避免产生脏分类
                if (!catById.TryGetValue(rule.CategoryId, out var catName))
                    continue;

                // 进程名非空才进进程精确匹配表；同进程多条规则时后者覆盖前者（最后写入生效）
                if (!string.IsNullOrWhiteSpace(rule.ProcessName))
                {
                    processRules[rule.ProcessName] = catName;

                    // 如果规则分类是"浏览器"，把进程加入浏览器集合
                    // 这样用户自定义的浏览器进程也能走"按标题关键词细分"的逻辑
                    if (rule.CategoryId == webCatId)
                        browsers.Add(rule.ProcessName);
                }

                // 标题关键词非空才进关键词规则列表（可与进程规则同时存在，互不冲突）
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
            // 至少保证资源管理器与两大浏览器有基本归类，避免全部落到"未分类"
            processRules["explorer"] = "系统组件";
            processRules["chrome"] = "浏览器";
            processRules["msedge"] = "浏览器";
        }

        // 原子替换缓存
        // 三次引用赋值各自原子生效；读端在 Classify 里整体抓快照，不会读到半新半旧的组合
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
        // 进程名为空说明拿不到前台窗口信息，无从匹配，直接归为未分类
        if (string.IsNullOrEmpty(processName))
            return "未分类";

        // 先抓一份字段快照，避免读取过程中字段被 ReloadRules 原子替换导致的不一致
        var processRules = _processRules;
        var titleKeywordRules = _titleKeywordRules;
        var browsers = _browsers;

        // 1. 先按进程名精确匹配（最快）
        // 命中即返回，绝大多数本地应用在这一步就能定类
        if (processRules.TryGetValue(processName, out var category))
            return category;

        // 2. 浏览器特殊处理 — 按标题关键词分类（因为同一个浏览器可能在做不同的事）
        if (browsers.Contains(processName))
        {
            // 遍历标题关键词规则：网页标题通常含站点/产品名，用"包含"做模糊匹配
            foreach (var (keyword, cat) in titleKeywordRules)
            {
                // 忽略大小写的子串匹配，任一关键词命中即返回其类别
                if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }
            return "浏览器"; // 浏览器但没匹配到关键词
        }

        // 3. 非浏览器按标题关键词兜底
        // 进程没配规则但窗口标题里带了已知关键词（如某些套壳应用），也能正确归类
        foreach (var (keyword, cat) in titleKeywordRules)
        {
            if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return cat;
        }

        // 所有层级都没命中 → 未分类，等待用户后续手动补规则
        return "未分类";
    }
}
