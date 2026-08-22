using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using TimeActivity.Data;
using TimeActivity.Models;
using TimeActivity.Services;

namespace TimeActivity;

// ============================================================================
// SettingsWindow.Rules.cs — 设置窗口的"分类规则"部分类
// 职责：
//   1) LoadRules：后台线程加载规则数据（只显示用户实际用过的进程，
//      没有规则的进程以"未分类"占位行呈现）；
//   2) BuildRulesPanel：按分类分组+搜索过滤后构建折叠分组面板；
//   3) SaveRules：把界面上的规则集合整体写回数据库；
//   4) 两个滚轮转发处理（让内部区域滚动接管到外层 ScrollViewer）。
// 协作对象：RuleRepository/ActivityRepository、_categories(内存分类缓存)、
//           CreateCategoryExpander(在 Categories 部分类中定义)。
// ============================================================================
public partial class SettingsWindow
{
    /// <summary>
    /// 加载全部规则（异步，避免阻塞 UI）：
    /// 有规则的进程 → 规则行；用户用过但无规则的进程 → "未分类"占位行。
    /// 完成后重建规则面板并刷新侧边栏计数。
    /// </summary>
    private async void LoadRules()
    {
        try
        {
            await Task.Run(() => // 后台线程执行查库与组装
            {
                // 获取用户实际使用过的进程名
                var usedProcesses = ActivityRepository.GetUsedProcessNames();
                var rules = RuleRepository.GetAll();       // 数据库中的全部分类规则
                var ruleItems = new List<RuleItem>();      // 组装结果集合
                foreach (var r in rules)
                {
                    // 只展示用户用过的应用
                    if (!usedProcesses.Contains(r.ProcessName)) continue;
                    var cat = _categories.FirstOrDefault(c => c.Id == r.CategoryId); // 规则→分类名
                    ruleItems.Add(new RuleItem
                    {
                        Id = r.Id,
                        ProcessName = r.ProcessName ?? "",
                        TitleKeyword = r.TitleKeyword ?? "",
                        CategoryName = cat?.Name ?? "",
                        IsCustom = r.IsCustom
                    });
                }
                // 用户用过但没有规则匹配的进程，显示为"未分类"
                var ruledProcessNames = new HashSet<string>(rules.Select(r => r.ProcessName), StringComparer.OrdinalIgnoreCase);
                foreach (var proc in usedProcesses)
                {
                    if (!ruledProcessNames.Contains(proc)) // 该进程没有任何规则
                    {
                        ruleItems.Add(new RuleItem
                        {
                            Id = 0,               // 0 表示尚未入库的占位规则
                            ProcessName = proc,
                            TitleKeyword = "",
                            CategoryName = "未分类",
                            IsCustom = false
                        });
                    }
                }
                _allRules = ruleItems; // 写入窗口级缓存供面板/保存使用
            });
            BuildRulesPanel();     // 用新数据重建 UI
            LoadCategorySidebar(); // 规则加载完后刷新侧边栏 Count（从内存算）
        }
        catch (Exception ex)
        {
            Logger.Error("LoadRules 加载失败", ex);
        }
    }

    /// <summary>
    /// 重建规则面板：按分类 SortOrder 分组，应用搜索关键词过滤，
    /// 为每个非空分组创建一个折叠 Expander。
    /// </summary>
    private void BuildRulesPanel()
    {
        if (RulesPanel == null) return; // XAML 未就绪时直接返回
        RulesPanel.Children.Clear();    // 清空旧内容

        // 搜索关键词过滤
        string keyword = TxtRuleSearch?.Text?.Trim().ToLower() ?? "";
        bool hasSearch = !string.IsNullOrWhiteSpace(keyword);

        // 按分类分组
        var grouped = _allRules
            .GroupBy(r => r.CategoryName)
            .OrderBy(g => _categories.FirstOrDefault(c => c.Name == g.Key)?.SortOrder ?? 999); // 无匹配分类的排最后

        foreach (var group in grouped)
        {
            var cat = _categories.FirstOrDefault(c => c.Name == group.Key); // 分组名→分类对象
            if (cat == null) continue; // 找不到分类（如"未分类"不在列表）则跳过

            // 搜索过滤
            var rulesInGroup = group.ToList();
            if (hasSearch) // 有关键词时按进程名/分类名模糊过滤
            {
                rulesInGroup = rulesInGroup
                    .Where(r => r.ProcessName.ToLower().Contains(keyword) || r.CategoryName.ToLower().Contains(keyword))
                    .ToList();
                if (rulesInGroup.Count == 0) continue; // 没匹配就跳过这个分组
            }

            // 创建折叠分组
            var expander = CreateCategoryExpander(cat, rulesInGroup, hasSearch);
            RulesPanel.Children.Add(expander);
        }
    }

    /// <summary>规则区滚轮转发：把滚轮增量转成 ScrollViewer 垂直滚动。</summary>
    private void RulesPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta); // Delta 上滚为正，故用减号
            e.Handled = true; // 阻止继续冒泡
        }
    }

    /// <summary>侧边栏滚轮转发：逻辑同上，作用于侧边栏滚动容器。</summary>
    private void CategorySidebar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 保存规则：构建 分类名→Id 映射后把全部规则整体写回数据库（含增删改）。
    /// </summary>
    private void SaveRules()
    {
        try
        {
            // 构建分类名→Id 映射
            var catMap = _categories.ToDictionary(c => c.Name, c => c.Id);
            RuleRepository.SaveAll(_allRules, catMap);
        }
        catch (Exception ex) { Logger.Error("SaveRules 保存失败", ex); }
    }

}
