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

        // CbxRuleFilter 已移除(新方案用折叠面板)

        LoadCategorySidebar();
    }

    private void LoadCategorySidebar()
    {
        if (CategorySidebar == null) return;
        var sidebarItems = new ObservableCollection<CategoryItem>();
        // 从内存 _allRules 算 Count(如果已加载),否则查一次数据库
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

    private Expander CreateCategoryExpander(CategoryItem cat, List<RuleItem> rules, bool forceExpand)
    {
        var expander = new Expander
        {
            Header = CreateCategoryHeader(cat, rules.Count),
            IsExpanded = forceExpand, // 搜索时全部展开,否则默认折叠
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(8, 4, 8, 4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Tag = forceExpand ? "search" : null, // 标记搜索模式
        };

        // 手风琴:展开一个收起其他(非搜索模式)
        expander.Expanded += (s, e) =>
        {
            if (expander.Tag as string == "search") return; // 搜索模式不折叠
            foreach (var child in RulesPanel.Children)
            {
                if (child is Expander other && other != expander && other.Tag as string != "search")
                    other.IsExpanded = false;
            }
        };

        // 内容:应用列表
        var itemsPanel = new StackPanel();
        foreach (var rule in rules)
        {
            var row = CreateAppRow(rule);
            itemsPanel.Children.Add(row);
        }
        expander.Content = itemsPanel;

        return expander;
    }

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

    private void AppRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string procName)
        {
            // Shift 范围选择(待实现,需记录视觉顺序)
            _lastClickedProcess = procName;
        }
    }

    private void AppRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is Border border && border.Tag is string procName)
        {
            // 如果有选中项,拖拽选中的;否则拖拽当前项
            var toDrag = _selectedProcessNames.Count > 0 ? _selectedProcessNames.ToList() : new List<string> { procName };
            var data = new DataObject();
            data.SetData("ProcessNames", toDrag);
            DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
        }
    }

    private void UpdateSelectionMode()
    {
        bool hasSelection = _selectedProcessNames.Count > 0;
        BtnExitSelect.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnExitSelect_Click(object sender, RoutedEventArgs e)
    {
        _selectedProcessNames.Clear();
        _lastClickedProcess = null;
        // 重建面板清除勾选状态
        BuildRulesPanel();
        UpdateSelectionMode();
    }

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

        // 找到目标分类:从 Drop 位置取 ListBoxItem
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

    private void BtnDeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CategoryItem cat)
        {
            if (cat.Id <= CategoryRepository.MaxPresetCategoryId) return; // 预置分类不可删

            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                cats.Remove(cat);
                MarkChanged();
            }
        }
    }

    private void CategoriesGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MarkChanged();
    }

    private void MarkChanged()
    {
        if (_loading) return;
        CheckHasChanges();
    }

}
