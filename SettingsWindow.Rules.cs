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

public partial class SettingsWindow
{
    private async void LoadRules()
    {
        try
        {
            await Task.Run(() =>
            {
                // 获取用户实际使用过的进程名
                var usedProcesses = ActivityRepository.GetUsedProcessNames();
                var rules = RuleRepository.GetAll();
                var ruleItems = new List<RuleItem>();
                foreach (var r in rules)
                {
                    // 只展示用户用过的应用
                    if (!usedProcesses.Contains(r.ProcessName)) continue;
                    var cat = _categories.FirstOrDefault(c => c.Id == r.CategoryId);
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
                    if (!ruledProcessNames.Contains(proc))
                    {
                        ruleItems.Add(new RuleItem
                        {
                            Id = 0,
                            ProcessName = proc,
                            TitleKeyword = "",
                            CategoryName = "未分类",
                            IsCustom = false
                        });
                    }
                }
                _allRules = ruleItems;
            });
            BuildRulesPanel();
            LoadCategorySidebar(); // 规则加载完后刷新侧边栏 Count（从内存算）
        }
        catch (Exception ex)
        {
            Logger.Error("LoadRules 加载失败", ex);
        }
    }

    private void BuildRulesPanel()
    {
        if (RulesPanel == null) return;
        RulesPanel.Children.Clear();

        // 搜索关键词过滤
        string keyword = TxtRuleSearch?.Text?.Trim().ToLower() ?? "";
        bool hasSearch = !string.IsNullOrWhiteSpace(keyword);

        // 按分类分组
        var grouped = _allRules
            .GroupBy(r => r.CategoryName)
            .OrderBy(g => _categories.FirstOrDefault(c => c.Name == g.Key)?.SortOrder ?? 999);

        foreach (var group in grouped)
        {
            var cat = _categories.FirstOrDefault(c => c.Name == group.Key);
            if (cat == null) continue;

            // 搜索过滤
            var rulesInGroup = group.ToList();
            if (hasSearch)
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

    private void RulesPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void CategorySidebar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

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
