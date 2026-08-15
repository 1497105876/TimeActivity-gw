using System;
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

public partial class SettingsWindow : Window
{
    private bool _loading = false;
    private bool _hasChanges = false;
    private Dictionary<string, string> _originalSettings = new();

    // 分类列表（用于规则下拉和分类管理）
    private List<CategoryItem> _categories = new();

    // 分类名列表（供 DataGridComboBoxColumn 绑定用）
    public List<string> _categoryNames = new();

    public SettingsWindow(string initialSection = null)
    {
        InitializeComponent();
        try
        {
            _loading = true;
            LoadSettings();
            LoadCategories();
            // LoadRules 延迟到用户切到分类规则页才加载
        }
        finally
        {
            _loading = false;
        }
        _hasChanges = false;
        SaveSnapshot(); // 记录初始快照，BtnApply 才能正常启用
        // 耗时操作异步执行
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateEstimates();
            UpdateDiskUsage();
        }), System.Windows.Threading.DispatcherPriority.Background);

        // 根据初始参数选中对应导航项
        if (!string.IsNullOrEmpty(initialSection))
        {
            int index = initialSection.ToLower() switch
            {
                "tracking" => 0,
                "screenshot" => 1,
                "rules" => 2,
                "categories" => 3,
                "data" => 4,
                "ai" => 5,
                "system" => 6,
                "io" => 7,
                _ => -1
            };
            if (index >= 0 && NavList != null)
            {
                NavList.SelectedIndex = index;
            }
        }
    }

    // ========== 侧边栏导航 ==========

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList == null || PanelTracking == null) return;

        // 先全部隐藏
        PanelTracking.Visibility = Visibility.Collapsed;
        PanelScreenshot.Visibility = Visibility.Collapsed;
        PanelRules.Visibility = Visibility.Collapsed;
        PanelCategories.Visibility = Visibility.Collapsed;
        PanelData.Visibility = Visibility.Collapsed;
        PanelAI.Visibility = Visibility.Collapsed;
        PanelSystem.Visibility = Visibility.Collapsed;
        PanelIO.Visibility = Visibility.Collapsed;

        // 根据选中索引显示对应面板（显示设置已删除，编号调整）
        switch (NavList.SelectedIndex)
        {
            case 0: PanelTracking.Visibility = Visibility.Visible; break;
            case 1: PanelScreenshot.Visibility = Visibility.Visible; break;
            case 2: PanelRules.Visibility = Visibility.Visible;
                    // 限制 PanelRules 高度让左右独立滚动
                    PanelRules.MaxHeight = Math.Max(300, SettingsScroll.ViewportHeight - 50);
                    // 延迟加载：首次切到分类规则页才加载规则
                    if (_allRules.Count == 0 && !_rulesLoaded)
                    {
                        _rulesLoaded = true;
                        LoadRules();
                    }
                    break;
            case 3: PanelCategories.Visibility = Visibility.Visible; break;
            case 4: PanelData.Visibility = Visibility.Visible; break;
            case 5: PanelAI.Visibility = Visibility.Visible; break;
            case 6: PanelSystem.Visibility = Visibility.Visible; break;
            case 7: PanelIO.Visibility = Visibility.Visible; break;
        }
    }

    // ========== 加载设置 ==========

    private void LoadSettings()
    {
        // 追踪设置
        SetComboByTagOrText(CbxSamplingInterval, SettingsRepository.Get("PollIntervalSeconds", "3"), "秒");
        SetComboByTagOrText(CbxIdleThreshold, SettingsRepository.Get("IdleThresholdSeconds", "300"), "分钟");
        // 如果 Tag 匹配不上（自定义值），把秒转回分钟显示
        if (CbxIdleThreshold.SelectedIndex == -1)
        {
            if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idleSec))
                CbxIdleThreshold.Text = (idleSec / 60).ToString();
        }
        ChkAutoStartTracking.IsChecked = SettingsRepository.Get("AutoStartTracking", "true") == "true";

        // 截图设置
        ChkEnableScreenshot.IsChecked = SettingsRepository.Get("EnableScreenshot", "false") == "true";
        ChkScreenshotOnSwitch.IsChecked = SettingsRepository.Get("ScreenshotOnSwitch", "true") == "true";

        string intervalStr = SettingsRepository.Get("ScreenshotIntervalMinutes", "5");
        SetComboByTagOrText(CbxScreenshotInterval, intervalStr, "分钟");

        // 截图格式
        string fmt = SettingsRepository.Get("ScreenshotFormat", "jpg");
        foreach (ComboBoxItem item in CbxScreenshotFormat.Items)
        {
            if (item.Tag?.ToString() == fmt) { CbxScreenshotFormat.SelectedItem = item; break; }
        }
        CbxScreenshotFormat.SelectedIndex = fmt == "png" ? 1 : 0;

        SelectComboByTag(CbxScreenshotQuality, SettingsRepository.Get("ScreenshotQuality", "medium"));
        TxtScreenshotPath.Text = SettingsRepository.Get("ScreenshotPath",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots"));

        // 存储限制
        ChkMaxSize.IsChecked = SettingsRepository.Get("EnableMaxSize", "true") == "true";
        TxtMaxSize.Text = SettingsRepository.Get("MaxScreenshotSizeMB", "5120");
        ChkMaxAge.IsChecked = SettingsRepository.Get("EnableMaxAge", "true") == "true";
        TxtMaxAge.Text = SettingsRepository.Get("MaxScreenshotAgeDays", "30");

        // 数据设置
        SetComboByTagOrText(CbxDataRetention, SettingsRepository.Get("DataRetentionDays", "90"), "天");

        // AI 设置
        ChkEnableAI.IsChecked = SettingsRepository.Get("EnableAI", "true") == "true";
        string aiMode = SettingsRepository.Get("AIMode", "lan");
        foreach (ComboBoxItem item in CbxAIMode.Items)
        {
            if (item.Tag?.ToString() == aiMode) { CbxAIMode.SelectedItem = item; break; }
        }
        TxtApiUrl.Text = SettingsRepository.Get("AIApiUrl", "");
        TxtApiKey.Password = SettingsRepository.Get("AIApiKey", "");
        TxtAIModel.Text = SettingsRepository.Get("AIModel", "");

        // AI 总结文件保存设置
        TxtAISummaryPath.Text = SettingsRepository.Get("AISummaryPath", "");
        TxtAISummaryMaxCount.Text = SettingsRepository.Get("AISummaryMaxCount", "0");
        TxtAISummaryMaxSizeMB.Text = SettingsRepository.Get("AISummaryMaxSizeMB", "0");

        // 系统设置
        ChkAutoStart.IsChecked = SettingsRepository.Get("AutoStartWithWindows", "false") == "true";
        ChkMinimizeToTray.IsChecked = SettingsRepository.Get("MinimizeToTray", "true") == "true";
    }

    // ========== 分类管理 ==========

    /// <summary>
    /// 从数据库加载分类列表，更新 DataGrid 和侧边栏
    /// </summary>
    private void LoadCategories()
    {
        _categories = new List<CategoryItem>();
        try
        {
            var cats = CategoryRepository.GetAll();
            foreach (var cat in cats)
            {
                _categories.Add(new CategoryItem { Id = cat.Id, Name = cat.Name, Color = cat.Color, SortOrder = cat.SortOrder });
            }
        }
        catch (Exception ex) { Logger.Error("LoadCategories 失败", ex); }

        CategoriesGrid.ItemsSource = new ObservableCollection<CategoryItem>(_categories);

        // 更新分类名列表供规则下拉用
        _categoryNames = _categories.Select(c => c.Name).ToList();

        // CbxRuleFilter 已移除（新方案用折叠面板）

        LoadCategorySidebar();
    }

    /// <summary>
    /// 加载左侧分类侧边栏（含每个分类的规则数）
    /// </summary>
    private void LoadCategorySidebar()
    {
        if (CategorySidebar == null) return;
        var sidebarItems = new ObservableCollection<CategoryItem>();
        // 从内存 _allRules 算 Count（如果已加载），否则查一次数据库
        if (_allRules.Count > 0)
        {
            foreach (var c in _categories)
            {
                int count = _allRules.Count(r => r.CategoryName == c.Name);
                sidebarItems.Add(new CategoryItem { Id = c.Id, Name = c.Name, Color = c.Color, SortOrder = c.SortOrder, Count = count });
            }
        }
        else
        {
            var dbRules = RuleRepository.GetAll();
            foreach (var c in _categories)
            {
                int count = dbRules.Count(r => r.CategoryId == c.Id);
                sidebarItems.Add(new CategoryItem { Id = c.Id, Name = c.Name, Color = c.Color, SortOrder = c.SortOrder, Count = count });
            }
        }
        CategorySidebar.ItemsSource = sidebarItems;
    }

    // ========== 分类规则（折叠面板版） ==========

    // 所有规则项（内存缓存）
    private List<RuleItem> _allRules = new();

    // 规则是否已加载（延迟加载，首次切到分类规则页才加载）
    private bool _rulesLoaded = false;

    // 多选的进程名集合
    private HashSet<string> _selectedProcessNames = new();

    // Shift 范围选择用的上次点击进程名
    private string? _lastClickedProcess = null;

    /// <summary>
    /// 异步加载分类规则：查库 + 补充未分类进程，构建内存缓存后渲染折叠面板
    /// </summary>
    private async void LoadRules()
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
            // 用户用过但没有规则匹配的进程，显示为“未分类”
            var ruleedProcessNames = new HashSet<string>(rules.Select(r => r.ProcessName), StringComparer.OrdinalIgnoreCase);
            foreach (var proc in usedProcesses)
            {
                if (!ruleedProcessNames.Contains(proc))
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

    /// <summary>
    /// 构建右侧折叠面板：按分类分组，支持搜索过滤
    /// </summary>
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

    /// <summary>
    /// 创建一个分类的折叠面板（Expander）：手风琴模式 + 搜索时全部展开
    /// </summary>
    private Expander CreateCategoryExpander(CategoryItem cat, List<RuleItem> rules, bool forceExpand)
    {
        var expander = new Expander
        {
            Header = CreateCategoryHeader(cat, rules.Count),
            IsExpanded = forceExpand, // 搜索时全部展开，否则默认折叠
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(8, 4, 8, 4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Tag = forceExpand ? "search" : null, // 标记搜索模式
        };

        // 手风琴：展开一个收起其他（非搜索模式）
        expander.Expanded += (s, e) =>
        {
            if (expander.Tag as string == "search") return; // 搜索模式不折叠
            foreach (var child in RulesPanel.Children)
            {
                if (child is Expander other && other != expander && other.Tag as string != "search")
                    other.IsExpanded = false;
            }
        };

        // 内容：应用列表
        var itemsPanel = new StackPanel();
        foreach (var rule in rules)
        {
            var row = CreateAppRow(rule);
            itemsPanel.Children.Add(row);
        }
        expander.Content = itemsPanel;

        return expander;
    }

    /// <summary>
    /// 创建分类头部 UI：色块 + 名称 + 数量
    /// </summary>
    private StackPanel CreateCategoryHeader(CategoryItem cat, int count)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var colorBox = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(cat.ColorValue)
        };
        header.Children.Add(colorBox);

        var name = new TextBlock
        {
            Text = cat.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(name);

        var countText = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(countText);

        return header;
    }

    /// <summary>
    /// 创建一个应用行 UI：复选框 + 图标 + 友好名，支持拖拽
    /// </summary>
    private Border CreateAppRow(RuleItem rule)
    {
        var displayName = AppDisplayName.Get(rule.ProcessName);
        var icon = IconExtractor.GetIcon(rule.ProcessName);

        var row = new Border
        {
            Padding = new Thickness(4, 3, 4, 3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Tag = rule.ProcessName, // 存进程名供拖拽和选择用
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        // CheckBox
        var checkbox = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsChecked = _selectedProcessNames.Contains(rule.ProcessName),
            Tag = rule.ProcessName,
        };
        checkbox.Checked += AppCheckbox_Changed;
        checkbox.Unchecked += AppCheckbox_Changed;
        panel.Children.Add(checkbox);

        // 图标
        if (icon != null)
        {
            var img = new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            panel.Children.Add(img);
        }
        else
        {
            // 没图标占位
            var placeholder = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                CornerRadius = new CornerRadius(2)
            };
            panel.Children.Add(placeholder);
        }

        // 友好名
        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(nameText);

        row.Child = panel;

        // 拖拽支持
        row.MouseMove += AppRow_MouseMove;
        row.MouseLeftButtonDown += AppRow_MouseLeftButtonDown;

        return row;
    }

    // ========== 多选逻辑 ==========

    /// <summary>应用复选框变化：更新选中集合并显示"退出选择"按钮</summary>
    private void AppCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string procName)
        {
            if (cb.IsChecked == true)
                _selectedProcessNames.Add(procName);
            else
                _selectedProcessNames.Remove(procName);
            UpdateSelectionMode();
            MarkChanged();
        }
    }

    /// <summary>应用行鼠标按下：记录点击的进程名（用于 Shift 范围选择）</summary>
    private void AppRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string procName)
        {
            // Shift 范围选择（待实现，需记录视觉顺序）
            _lastClickedProcess = procName;
        }
    }

    /// <summary>应用行鼠标移动：左键按下时启动拖拽操作</summary>
    private void AppRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is Border border && border.Tag is string procName)
        {
            // 如果有选中项，拖拽选中的；否则拖拽当前项
            var toDrag = _selectedProcessNames.Count > 0 ? _selectedProcessNames.ToList() : new List<string> { procName };
            var data = new DataObject();
            data.SetData("ProcessNames", toDrag);
            DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
        }
    }

    /// <summary>更新选择模式 UI：有选中项时显示"退出选择"按钮</summary>
    private void UpdateSelectionMode()
    {
        bool hasSelection = _selectedProcessNames.Count > 0;
        BtnExitSelect.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>退出选择按钮：清空所有选中并重建面板</summary>
    private void BtnExitSelect_Click(object sender, RoutedEventArgs e)
    {
        _selectedProcessNames.Clear();
        _lastClickedProcess = null;
        // 重建面板清除勾选状态
        BuildRulesPanel();
        UpdateSelectionMode();
    }

    // ========== 搜索 ==========

    // 搜索防抖定时器
    private DispatcherTimer? _searchDebounceTimer;

    /// <summary>搜索框文字变化：300ms 防抖后重建面板</summary>
    private void TxtRuleSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick -= SearchDebounce_Tick;
        _searchDebounceTimer.Tick += SearchDebounce_Tick;
        _searchDebounceTimer.Start();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer!.Stop();
        BuildRulesPanel();
    }

    // ========== 左侧分类列表交互 ==========

    /// <summary>点击左侧分类 → 展开右侧对应折叠分组</summary>
    private void CategorySidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategorySidebar == null) return;
        if (CategorySidebar.SelectedItem is CategoryItem cat)
        {
            // 点击左侧分类 → 展开右侧对应分组
            foreach (var child in RulesPanel.Children)
            {
                if (child is Expander exp && exp.Header is StackPanel header)
                {
                    // 找到分类名匹配的 Expander
                    var nameBlock = header.Children.OfType<TextBlock>().FirstOrDefault(t => t.FontWeight == FontWeights.Bold);
                    if (nameBlock?.Text == cat.Name)
                    {
                        exp.IsExpanded = true;
                        exp.BringIntoView();
                        break;
                    }
                }
            }
        }
    }

    // ========== 拖拽到左侧分类 ==========

    /// <summary>拖拽经过分类项时：高亮当前悬停项</summary>
    private void CategorySidebar_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ProcessNames"))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        // 高亮当前悬停的 ListBoxItem
        var dep = e.OriginalSource as DependencyObject;
        ListBoxItem? hoveredItem = null;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        hoveredItem = dep as ListBoxItem;

        foreach (var item in CategorySidebar.Items)
        {
            if (CategorySidebar.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
            {
                if (lbi == hoveredItem && lbi.DataContext is CategoryItem)
                    lbi.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD));
                else
                    lbi.Background = Brushes.Transparent;
            }
        }
        e.Handled = true;
    }

    /// <summary>拖拽进入：设置拖拽效果</summary>
    private void CategorySidebar_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ProcessNames"))
        {
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>拖拽离开：清除所有高亮</summary>
    private void CategorySidebar_DragLeave(object sender, DragEventArgs e)
    {
        // 清除所有项的高亮
        foreach (var item in CategorySidebar.Items)
        {
            if (CategorySidebar.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
            {
                lbi.Background = Brushes.Transparent;
            }
        }
    }

    /// <summary>拖拽放下：把拖来的进程改到目标分类</summary>
    private void CategorySidebar_Drop(object sender, DragEventArgs e)
    {
        // 清除高亮
        foreach (var item in CategorySidebar.Items)
        {
            if (CategorySidebar.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
            {
                lbi.Background = Brushes.Transparent;
            }
        }

        if (!e.Data.GetDataPresent("ProcessNames")) return;
        var procNames = e.Data.GetData("ProcessNames") as List<string>;
        if (procNames == null || procNames.Count == 0) return;

        // 找到目标分类：从 Drop 位置取 ListBoxItem
        CategoryItem? targetCat = null;
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem lbiItem && lbiItem.DataContext is CategoryItem cat)
            targetCat = cat;
        else if (CategorySidebar.SelectedItem is CategoryItem selectedCat)
            targetCat = selectedCat;

        if (targetCat == null) return;

        // 改分类
        int changed = 0;
        foreach (var procName in procNames)
        {
            var rule = _allRules.FirstOrDefault(r => r.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase));
            if (rule != null && rule.CategoryName != targetCat.Name)
            {
                rule.CategoryName = targetCat.Name;
                rule.IsCustom = true;
                changed++;
            }
        }

        if (changed > 0)
        {
            // 重建面板 + 刷新侧边栏 Count
            BuildRulesPanel();
            LoadCategorySidebar();
            MarkChanged();
        }
    }

    /// <summary>规则面板滚轮事件处理：手动滚动避免嵌套 ScrollViewer 冲突</summary>
    private void RulesPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>分类侧边栏滚轮事件处理</summary>
    private void CategorySidebar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // ========== 保存设置 ==========

    /// <summary>设置保存后的事件通知（主窗口订阅后重启服务、重载规则等）</summary>
    public static event Action? SettingsSaved;

    /// <summary>保存按钮：保存设置并关窗</summary>
    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DoSave();
        MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        this.Close();
    }

    /// <summary>
    /// 保存所有分类规则到数据库（用事务批量写入，避免逐条开关连接）
    /// </summary>
    private void SaveRules()
    {
        try
        {
            // 只更新/插入用户改过的规则，不删预置规则
            // 共用一个连接 + 事务，避免逐条开关连接
            using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
            conn.Open();
            using var transaction = conn.BeginTransaction();

            foreach (var r in _allRules)
            {
                if (string.IsNullOrWhiteSpace(r.ProcessName))
                    continue;
                var cat = _categories.FirstOrDefault(c => c.Name == r.CategoryName);
                if (cat == null) continue;

                if (r.Id > 0)
                {
                    // 已有规则，更新分类
                    using var upd = new SqliteCommand(
                        "UPDATE Rules SET CategoryId=@c, IsCustom=@ic WHERE Id=@id", conn, transaction);
                    upd.Parameters.AddWithValue("@c", cat.Id);
                    upd.Parameters.AddWithValue("@ic", r.IsCustom ? 1 : 0);
                    upd.Parameters.AddWithValue("@id", r.Id);
                    upd.ExecuteNonQuery();
                }
                else
                {
                    // 新规则（用户用过但之前没规则匹配的进程）
                    using var ins = new SqliteCommand(
                        "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@p, @k, @c, @ic)", conn, transaction);
                    ins.Parameters.AddWithValue("@p", r.ProcessName ?? "");
                    ins.Parameters.AddWithValue("@k", (object?)r.TitleKeyword ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@c", cat.Id);
                    ins.Parameters.AddWithValue("@ic", r.IsCustom ? 1 : 0);
                    ins.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch (Exception ex) { Logger.Error("SaveRules 保存失败", ex); }
    }

    /// <summary>
    /// 保存分类管理数据：更新/新增/删除分类（预置分类 Id 1-13 不可删）
    /// </summary>
    private void SaveCategories()
    {
        try
        {
            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                // 收集当前 UI 中存在的分类 Id
                var currentIds = new HashSet<int>();
                foreach (var c in cats)
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    currentIds.Add(c.Id);
                    // Id<=0 表示新分类，插入后获新 Id
                    // Id>0 更新已有分类
                    CategoryRepository.UpdateOrInsert(c.Id, c.Name, c.Color ?? "#808080", c.SortOrder);
                }

                // 删除用户在 UI 中删掉的自定义分类（预置 Id<=13 不可删）
                var dbCats = CategoryRepository.GetAll();
                foreach (var dbCat in dbCats)
                {
                    if (dbCat.Id > 13 && !currentIds.Contains(dbCat.Id))
                    {
                        CategoryRepository.Delete(dbCat.Id);
                    }
                }
            }
        }
        catch (Exception ex) { Logger.Error("SaveCategories 失败", ex); }
    }

    // ========== 删行按钮 + 颜色选择器 ==========

    // BtnDeleteRule_Click 已移除（新方案无删除按钮，改分类用拖拽）

    /// <summary>删除分类按钮：预置分类不可删，只删自定义分类</summary>
    private void BtnDeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CategoryItem cat)
        {
            if (cat.Id <= 13) return; // 预置分类不可删

            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                cats.Remove(cat);
                MarkChanged();
            }
        }
    }

    /// <summary>
    /// 颜色选择器按钮：弹出系统选色对话框，选好后更新分类颜色并刷新 UI
    /// </summary>
    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not CategoryItem item) return;

        // 用 WinForms ColorDialog（系统自带选色器，效果好）
        var dlg = new System.Windows.Forms.ColorDialog();
        dlg.FullOpen = true; // 展开完整选色面板
        try
        {
            // 当前颜色设进去
            var current = (Color)ColorConverter.ConvertFromString(item.Color ?? "#808080");
            dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch (Exception ex) { Logger.Error($"颜色解析失败: {item.Color}", ex); }

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            item.Color = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            // 刷新绑定
            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                var idx = cats.IndexOf(item);
                if (idx >= 0)
                {
                    var tmp = cats[idx];
                    cats[idx] = new CategoryItem { Id = tmp.Id, Name = tmp.Name, Color = item.Color, SortOrder = tmp.SortOrder };
                }
            }
            // 同步刷新分类规则页的侧边栏和面板颜色
            if (_rulesLoaded)
            {
                // 更新 _categories 内存中的颜色
                var cat = _categories.FirstOrDefault(c => c.Name == item.Name);
                if (cat != null) cat.Color = item.Color;
                LoadCategorySidebar();
                BuildRulesPanel();
            }
            MarkChanged();
        }
    }

    /// <summary>取消按钮：不保存直接关窗</summary>
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // 不保存直接关窗
        this.Close();
    }

    /// <summary>应用按钮：保存设置但不关窗</summary>
    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        // 和保存一样逻辑，但不关窗
        DoSave();
    }

    /// <summary>
    /// 实际保存逻辑：把 UI 上的值写回设置仓库、保存规则和分类、通知主窗口
    /// </summary>
    private void DoSave()
    {
        // 追踪设置：采样间隔和空闲阈值
        string samplingText = CbxSamplingInterval.Text.Replace("秒", "").Trim();
        if (int.TryParse(samplingText, out int sv) && sv > 0)
            SettingsRepository.Set("PollIntervalSeconds", sv.ToString());
        else
            SettingsRepository.Set("PollIntervalSeconds", "3");

        string idleText = CbxIdleThreshold.Text.Replace("分钟", "").Trim();
        if (int.TryParse(idleText, out int iv) && iv > 0)
            SettingsRepository.Set("IdleThresholdSeconds", (iv * 60).ToString());
        else
            SettingsRepository.Set("IdleThresholdSeconds", "300");

        SettingsRepository.Set("AutoStartTracking", ChkAutoStartTracking.IsChecked == true ? "true" : "false");

        // 截图设置：开关、间隔、格式、质量、路径、存储限制
        SettingsRepository.Set("EnableScreenshot", ChkEnableScreenshot.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("ScreenshotOnSwitch", ChkScreenshotOnSwitch.IsChecked == true ? "true" : "false");

        // 截图间隔
        string intervalText = CbxScreenshotInterval.Text.Replace("分钟", "").Trim();
        if (int.TryParse(intervalText, out int intervalVal) && intervalVal > 0)
            SettingsRepository.Set("ScreenshotIntervalMinutes", intervalVal.ToString());
        else
            SettingsRepository.Set("ScreenshotIntervalMinutes", "5");

        SettingsRepository.Set("ScreenshotFormat", CbxScreenshotFormat.SelectedItem is ComboBoxItem fmtItem ? fmtItem.Tag?.ToString() ?? "jpg" : "jpg");
        SettingsRepository.Set("ScreenshotQuality", GetComboTag(CbxScreenshotQuality));
        SettingsRepository.Set("ScreenshotPath", TxtScreenshotPath.Text);

        SettingsRepository.Set("EnableMaxSize", ChkMaxSize.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MaxScreenshotSizeMB",
            int.TryParse(TxtMaxSize.Text, out int ms) && ms > 0 ? ms.ToString() : "5120");
        SettingsRepository.Set("EnableMaxAge", ChkMaxAge.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MaxScreenshotAgeDays",
            int.TryParse(TxtMaxAge.Text, out int ma) && ma > 0 ? ma.ToString() : "30");

        // 数据设置
        string retentionText = CbxDataRetention.Text.Replace("天", "").Replace("永久", "0").Trim();
        if (int.TryParse(retentionText, out int dr) && dr >= 0)
            SettingsRepository.Set("DataRetentionDays", dr.ToString());
        else
            SettingsRepository.Set("DataRetentionDays", "90");

        // AI 设置
        SettingsRepository.Set("EnableAI", ChkEnableAI.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("AIMode", CbxAIMode.SelectedItem is ComboBoxItem aiItem ? aiItem.Tag?.ToString() ?? "lan" : "lan");
        SettingsRepository.Set("AIApiUrl", TxtApiUrl.Text);
        SettingsRepository.Set("AIApiKey", TxtApiKey.Password);
        SettingsRepository.Set("AIModel", TxtAIModel.Text);

        // AI 总结文件保存
        SettingsRepository.Set("AISummaryPath", TxtAISummaryPath.Text);
        SettingsRepository.Set("AISummaryMaxCount", TxtAISummaryMaxCount.Text);
        SettingsRepository.Set("AISummaryMaxSizeMB", TxtAISummaryMaxSizeMB.Text);

        // 系统设置
        SettingsRepository.Set("AutoStartWithWindows", ChkAutoStart.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MinimizeToTray", ChkMinimizeToTray.IsChecked == true ? "true" : "false");

        // 开机自启设置
        if (ChkAutoStart.IsChecked == true)
            AutoStartHelper.Enable();
        else
            AutoStartHelper.Disable();

        // 保存分类规则和分类管理
        SaveRules();
        SaveCategories();

        // 刷新侧边栏 Count
        LoadCategorySidebar();

        // 通知主窗口重启服务 + 重新分类历史数据
        SettingsSaved?.Invoke();

        // 刷新预估值和磁盘占用
        UpdateEstimates();
        UpdateDiskUsage();

        _hasChanges = false;
        TxtUnsaved.Text = "";
        BtnApply.IsEnabled = false;
        SaveSnapshot();
    }

    /// <summary>
    /// 保存当前设置的快照，用于后续对比是否有变更
    /// </summary>
    private void SaveSnapshot()
    {
        _originalSettings = GetCurrentSettingsSnapshot();
    }

    /// <summary>
    /// 收集当前 UI 上所有设置项的值，用于变更检测
    /// </summary>
    private Dictionary<string, string> GetCurrentSettingsSnapshot()
    {
        var snap = new Dictionary<string, string>();
        snap["PollIntervalSeconds"] = CbxSamplingInterval.Text ?? "";
        snap["IdleThresholdSeconds"] = CbxIdleThreshold.Text ?? "";
        snap["AutoStartTracking"] = (ChkAutoStartTracking.IsChecked == true).ToString().ToLower();
        snap["EnableScreenshot"] = (ChkEnableScreenshot.IsChecked == true).ToString().ToLower();
        snap["ScreenshotOnSwitch"] = (ChkScreenshotOnSwitch.IsChecked == true).ToString().ToLower();
        snap["ScreenshotIntervalMinutes"] = CbxScreenshotInterval.Text ?? "";
        snap["ScreenshotQuality"] = GetComboTag(CbxScreenshotQuality);
        snap["ScreenshotFormat"] = GetComboTag(CbxScreenshotFormat);
        snap["ScreenshotPath"] = TxtScreenshotPath.Text;
        snap["EnableMaxSize"] = (ChkMaxSize.IsChecked == true).ToString().ToLower();
        snap["MaxScreenshotSizeMB"] = TxtMaxSize.Text;
        snap["EnableMaxAge"] = (ChkMaxAge.IsChecked == true).ToString().ToLower();
        snap["MaxScreenshotAgeDays"] = TxtMaxAge.Text;
        snap["DataRetentionDays"] = CbxDataRetention.Text ?? "";
        snap["EnableAI"] = (ChkEnableAI.IsChecked == true).ToString().ToLower();
        snap["AIMode"] = GetComboTag(CbxAIMode);
        snap["AIApiUrl"] = TxtApiUrl.Text;
        snap["AIApiKey"] = TxtApiKey.Password;
        snap["AIModel"] = TxtAIModel.Text;
        snap["AISummaryPath"] = TxtAISummaryPath.Text;
        snap["AISummaryMaxCount"] = TxtAISummaryMaxCount.Text;
        snap["AISummaryMaxSizeMB"] = TxtAISummaryMaxSizeMB.Text;
        snap["AutoStartWithWindows"] = (ChkAutoStart.IsChecked == true).ToString().ToLower();
        snap["MinimizeToTray"] = (ChkMinimizeToTray.IsChecked == true).ToString().ToLower();

        // 规则和分类数据
        snap["__rules"] = JsonSerializer.Serialize(_allRules.Select(r => new { r.ProcessName, r.TitleKeyword, r.CategoryName }).ToList());
        if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            snap["__categories"] = JsonSerializer.Serialize(cats.Select(c => new { c.Name, c.Color, c.SortOrder }).ToList());

        return snap;
    }

    /// <summary>
    /// 对比当前值和快照，检查是否有变更，更新未保存提示和应用按钮状态
    /// </summary>
    private void CheckHasChanges()
    {
        if (_loading || _originalSettings.Count == 0) return;
        var current = GetCurrentSettingsSnapshot();
        bool changed = false;
        foreach (var kvp in _originalSettings)
        {
            if (!current.TryGetValue(kvp.Key, out var val) || val != kvp.Value)
            {
                changed = true;
                break;
            }
        }
        _hasChanges = changed;
        TxtUnsaved.Text = changed ? "有未保存的更改" : "";
        BtnApply.IsEnabled = changed;
    }

    private static string GetComboTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
            return item.Tag.ToString() ?? "";
        return combo.Text ?? "";
    }

    /// <summary>浏览截图保存路径按钮</summary>
    private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存路径",
            InitialDirectory = TxtScreenshotPath.Text ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            TxtScreenshotPath.Text = dialog.FolderName;
            UpdateDiskUsage();
            MarkChanged();
        }
    }

    // ========== AI 总结路径浏览 ==========

    /// <summary>浏览 AI 总结保存路径按钮</summary>
    private void BtnBrowseAISummaryPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 AI 总结保存路径",
            InitialDirectory = TxtAISummaryPath.Text ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            TxtAISummaryPath.Text = dialog.FolderName;
            MarkChanged();
        }
    }

    // ========== AI 测试连接 ==========

    /// <summary>测试 AI API 连接是否可用</summary>
    private async void BtnTestAI_Click(object sender, RoutedEventArgs e)
    {
        string apiUrl = TxtApiUrl.Text.Trim();
        string apiKey = TxtApiKey.Password;
        string model = TxtAIModel.Text.Trim();
        string mode = (CbxAIMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "lan";

        if (string.IsNullOrEmpty(apiUrl))
        {
            MessageBox.Show("请先填写服务地址", "提示");
            return;
        }

        BtnTestAI.Content = "测试中...";
        BtnTestAI.IsEnabled = false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            if (mode == "lan")
            {
                // Ollama 模式：GET /api/tags 检测在线
                var resp = await http.GetAsync($"{apiUrl.TrimEnd('/')}/api/tags");
                if (resp.IsSuccessStatusCode)
                    MessageBox.Show($"连接成功！Ollama 服务正常运行。\n模型名：{model}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"连接失败，HTTP {resp.StatusCode}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // 自定义模式：POST 一个简单消息测试
                if (!string.IsNullOrEmpty(apiKey))
                    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                var payload = $"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}],\"max_tokens\":10}}";
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(apiUrl, content);

                if (resp.IsSuccessStatusCode)
                    MessageBox.Show($"连接成功！API 可正常调用。\n模型名：{model}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"连接失败，HTTP {resp.StatusCode}\n{await resp.Content.ReadAsStringAsync()}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("连接超时，请检查服务是否已启动", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败：{ex.Message}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnTestAI.Content = "测试连接";
            BtnTestAI.IsEnabled = true;
        }
    }

    // ========== 预估占用大小 ==========

    /// <summary>根据截图格式和质量预估单张截图大小（KB）</summary>
    private int GetEstPerShotKB()
    {
        // PNG 比 JPEG 大很多
        bool isPng = CbxScreenshotFormat.SelectedItem is ComboBoxItem fi && fi.Tag?.ToString() == "png";
        if (isPng) return 500; // PNG 2560x1440 约 500KB
        return GetComboTag(CbxScreenshotQuality) switch
        {
            "high" => 150,
            "low" => 40,
            _ => 80
        };
    }

    /// <summary>根据实际屏幕分辨率调整预估大小</summary>
    private int GetActualScreenKB()
    {
        try
        {
            int w = ScreenshotService.GetScreenWidth();
            int h = ScreenshotService.GetScreenHeight();
            double ratio = (double)(w * h) / (2560 * 1440);
            return (int)(GetEstPerShotKB() * ratio);
        }
        catch (Exception ex)
        {
            Logger.Error("获取屏幕大小失败", ex);
            return GetEstPerShotKB();
        }
    }

    /// <summary>
    /// 更新截图大小和每日占用预估显示
    /// </summary>
    private void UpdateEstimates()
    {
        int perShotKB = GetActualScreenKB();
        TxtEstSize.Text = $"约 {perShotKB / 1024.0:F1} MB ({perShotKB} KB)";

        string intervalText = CbxScreenshotInterval.Text.Replace("分钟", "").Trim();
        int intervalMin = int.TryParse(intervalText, out int iv) && iv > 0 ? iv : 5;

        int timedShots = 1440 / intervalMin;
        bool onSwitch = ChkScreenshotOnSwitch.IsChecked == true;
        int switchShots = onSwitch ? 100 : 0;

        int totalShots = timedShots + switchShots;
        int dailyKB = totalShots * perShotKB;
        double dailyMB = dailyKB / 1024.0;

        string dailyStr = dailyMB >= 1024
            ? $"约 {dailyMB / 1024.0:F1} GB/天 ({totalShots} 张)"
            : $"约 {dailyMB:F0} MB/天 ({totalShots} 张)";

        if (ChkMaxSize.IsChecked == true && int.TryParse(TxtMaxSize.Text, out int maxMB) && maxMB > 0)
        {
            double days = maxMB / dailyMB;
            dailyStr += $" → {maxMB}MB 约可用 {days:F0} 天";
        }
        if (ChkMaxAge.IsChecked == true && int.TryParse(TxtMaxAge.Text, out int maxAge) && maxAge > 0)
        {
            double totalMB = dailyMB * maxAge;
            string totalStr = totalMB >= 1024 ? $"{totalMB / 1024.0:F1} GB" : $"{totalMB:F0} MB";
            dailyStr += $" → {maxAge}天 约需 {totalStr}";
        }

        TxtEstDaily.Text = dailyStr;
    }

    /// <summary>
    /// 扫描截图目录计算实际磁盘占用和文件数
    /// </summary>
    private void UpdateDiskUsage()
    {
        try
        {
            string dir = TxtScreenshotPath.Text;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                long totalBytes = 0;
                int fileCount = 0;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        totalBytes += new FileInfo(f).Length;
                        fileCount++;
                    }
                }
                double mb = totalBytes / (1024.0 * 1024.0);
                TxtDiskUsage.Text = mb >= 1024
                    ? $"{mb / 1024.0:F1} GB ({fileCount} 张)"
                    : $"{mb:F0} MB ({fileCount} 张)";
            }
            else
            {
                TxtDiskUsage.Text = "文件夹不存在";
            }
        }
        catch (Exception ex)
        {
            Logger.Error("截图磁盘占用计算失败", ex);
            TxtDiskUsage.Text = "-";
        }
    }

    // ========== AI 模式切换 ==========

    /// <summary>AI 模式下拉框变化：局域网模式预填 Ollama 地址，自定义模式清空</summary>
    private void AIMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string mode = CbxAIMode.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "lan" : "lan";
        if (mode == "lan")
        {
            // 局域网共享模式：默认 Ollama 地址
            if (string.IsNullOrWhiteSpace(TxtApiUrl.Text) || TxtApiUrl.Text.Contains("minimax") || TxtApiUrl.Text.Contains("openai"))
            {
                TxtApiUrl.Text = "http://localhost:11434";
                TxtApiKey.Password = "";
                TxtAIModel.Text = "qwen2.5:7b";
            }
        }
        else
        {
            // 自定义模式：如果当前是 Ollama 地址就清空让用户填
            if (TxtApiUrl.Text.Contains("localhost:11434"))
            {
                TxtApiUrl.Text = "";
                TxtAIModel.Text = "";
            }
        }
        MarkChanged();
    }

    // ========== 事件 ==========

    /// <summary>截图间隔变化：更新预估并标记变更</summary>
    private void CbxScreenshotInterval_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateEstimates();
        MarkChanged();
    }

    /// <summary>存储限制变化：更新预估并标记变更</summary>
    private void StorageLimit_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateEstimates();
        MarkChanged();
    }

    /// <summary>数字输入框过滤：只允许输入数字</summary>
    private void NumberOnly_Preview(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c)) { e.Handled = true; return; }
        }
    }

    /// <summary>分类表格变化事件：标记变更</summary>
    private void CategoriesGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MarkChanged();
    }

    // ========== 未保存提示 ==========

    /// <summary>标记有变更（加载期间除外），触发变更检测</summary>
    private void MarkChanged()
    {
        if (_loading) return;
        CheckHasChanges();
    }

    // ========== 备份数据库 ==========

    /// <summary>备份数据库按钮：用 VACUUM INTO 导出副本，不需要停引擎</summary>
    private void BtnBackupDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "SQLite 数据库|*.db|所有文件|*.*",
                FileName = $"timeactivity_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db"
            };
            if (dlg.ShowDialog() != true) return;

            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");
            if (!System.IO.File.Exists(dbPath))
            {
                MessageBox.Show("数据库文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 用 VACUUM INTO 备份，不需要停引擎
            DatabaseHelper.BackupTo(dlg.FileName);
            MessageBox.Show($"备份成功！\n{dlg.FileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("数据库备份失败", ex);
            MessageBox.Show($"备份失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ========== 清空数据 ==========

    /// <summary>清空所有数据按钮：两次确认后清空活动记录、截图和统计数据</summary>
    private void BtnClearData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要清空所有活动记录、截图和统计数据吗？\n此操作不可恢复！",
            "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var result2 = MessageBox.Show(
            "再次确认：真的要删除所有数据吗？",
            "再次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result2 != MessageBoxResult.Yes) return;

        DatabaseHelper.ClearAllData();
        UpdateDiskUsage();
        MessageBox.Show("所有数据已清空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ========== 恢复此页默认 ==========

    /// <summary>恢复当前页面默认值按钮：根据选中的面板恢复对应设置</summary>
    private void BtnRestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        string pageName = NavList.SelectedIndex switch
        {
            0 => "追踪设置",
            1 => "截图设置",
            2 => "分类规则",
            3 => "分类管理",
            4 => "数据设置",
            5 => "AI 设置",
            6 => "系统设置",
            7 => "导入/导出",
            _ => "当前页"
        };

        // 分类规则页恢复默认会清空用户自定义规则，需额外提醒
        string hint = NavList.SelectedIndex switch
        {
            2 => "恢复「分类规则」到默认值将清空所有自定义分类映射，此操作不可撤销。\n\n确定继续？",
            3 => "恢复「分类管理」将删除所有自定义分类并重置预置分类颜色。\n\n确定继续？",
            _ => $"恢复「{pageName}」到默认值并保存？"
        };

        var result = MessageBox.Show(hint, "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        switch (NavList.SelectedIndex)
        {
            case 0: // 追踪设置
            case 1: // 截图设置
            case 4: // 数据设置
            case 5: // AI 设置
            case 6: // 系统设置
                foreach (var kv in SettingsRepository.GetDefaultsByPage(NavList.SelectedIndex))
                    SettingsRepository.Set(kv.Key, kv.Value);
                break;

            case 2: // 分类规则
                RuleRepository.ClearAll();
                _rulesLoaded = false;
                _allRules.Clear();
                if (PanelRules.Visibility == Visibility.Visible)
                {
                    _rulesLoaded = true;
                    LoadRules();
                }
                break;

            case 3: // 分类管理
                CategoryRepository.ResetToDefault();
                LoadCategories();
                break;

            case 7: // 导入/导出
                MessageBox.Show("导入/导出页无需恢复默认", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
        }

        _loading = true;
        LoadSettings();
        if (_rulesLoaded && NavList.SelectedIndex == 2) LoadRules();
        if (NavList.SelectedIndex == 3) LoadCategories();
        UpdateEstimates();
        UpdateDiskUsage();
        _loading = false;

        // 直接保存（不关窗）
        DoSave();
        TxtUnsaved.Text = "✓ 已恢复默认并保存";
    }

    // ========== 导入导出 ==========

    /// <summary>导出设置到 JSON 文件</summary>
    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出设置",
            Filter = "JSON 文件|*.json",
            FileName = "timeactivity_settings.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var settings = SettingsRepository.GetAll();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
            MessageBox.Show($"设置已导出到\n{dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>从 JSON 文件导入设置</summary>
    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入设置",
            Filter = "JSON 文件|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (settings == null) { MessageBox.Show("文件内容为空", "错误"); return; }

            foreach (var kv in settings)
                SettingsRepository.Set(kv.Key, kv.Value);

            _loading = true;
            LoadSettings();
            LoadCategories();
            if (_rulesLoaded) LoadRules();
            UpdateEstimates();
            UpdateDiskUsage();
            _loading = false;
            _hasChanges = false;
            TxtUnsaved.Text = "";
            MessageBox.Show("设置已导入", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ========== 辅助方法 ==========

    /// <summary>按 Tag 匹配选中 ComboBox 项</summary>
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    /// <summary>
    /// 用 Tag 或文本匹配 ComboBox 项，匹配不上就把文本写进去（适用于可编辑 ComboBox）
    /// </summary>
    private static void SetComboByTagOrText(ComboBox combo, string value, string suffix = "")
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Content?.ToString()?.Replace(suffix, "").Trim() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        // 匹配不上：直接写文本（可编辑模式）
        combo.Text = value;
    }
}

