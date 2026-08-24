// 引用的命名空间（与各部分类文件保持一致的 using 集）
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
// SettingsWindow.Categories.cs — 设置窗口的"分类管理与规则面板构建"部分类
// 职责：
//   1) LoadCategories/LoadCategorySidebar：加载分类到网格与侧边栏(带规则计数)；
//   2) 构建规则折叠面板：分类 Expander、分组头、应用行(复选框+图标+友好名)；
//   3) 应用行多选与拖拽：勾选集合维护、拖拽到侧边栏改分类；
//   4) 搜索防抖、侧边栏联动展开分组；
//   5) 分类删除(预置不可删)、脏标记(MarkChanged)。
// 协作对象：CategoryRepository/RuleRepository、AppDisplayName/IconExtractor、
//           BuildRulesPanel(Rules 部分类)、CheckHasChanges(Save 部分类)。
// ============================================================================
public partial class SettingsWindow
{
    /// <summary>
    /// 从数据库加载全部分类，填充编辑网格与内存缓存，
    /// 并刷新分类名列表（供规则下拉等使用）和侧边栏。
    /// </summary>
    private void LoadCategories()
    {
        _categories = new List<CategoryItem>(); // 重置内存缓存
        try
        {
            var cats = CategoryRepository.GetAll(); // 读库
            foreach (var cat in cats)
            {
                // 数据模型 → 显示模型 一一转换
                _categories.Add(new CategoryItem { Id = cat.Id, Name = cat.Name, Color = cat.Color, SortOrder = cat.SortOrder });
            }
        }
        catch (Exception ex) { Logger.Error("LoadCategories 失败", ex); } // 失败时保持空列表

        // 包装为可观察集合：网格内的新增行能即时生效并参与后续保存
        CategoriesGrid.ItemsSource = new ObservableCollection<CategoryItem>(_categories); // 绑定可编辑网格

        // 更新分类名列表供规则下拉用
        _categoryNames = _categories.Select(c => c.Name).ToList();

        // CbxRuleFilter 已移除(新方案用折叠面板)

        LoadCategorySidebar();
    }

    /// <summary>
    /// 刷新左侧分类侧边栏：每项带"该分类下规则数"。
    /// 规则已加载时直接从内存统计；否则回退查一次数据库。
    /// </summary>
    private void LoadCategorySidebar()
    {
        if (CategorySidebar == null) return; // 控件未就绪
        var sidebarItems = new ObservableCollection<CategoryItem>();
        // 从内存 _allRules 算 Count(如果已加载),否则查一次数据库
        if (_allRules.Count > 0)
        {
            foreach (var c in _categories)
            {
                int count = _allRules.Count(r => r.CategoryName == c.Name); // 内存统计规则数
                sidebarItems.Add(new CategoryItem { Id = c.Id, Name = c.Name, Color = c.Color, SortOrder = c.SortOrder, Count = count });
            }
        }
        else
        {
            var dbRules = RuleRepository.GetAll(); // 兜底：查库统计
            foreach (var c in _categories)
            {
                int count = dbRules.Count(r => r.CategoryId == c.Id);
                sidebarItems.Add(new CategoryItem { Id = c.Id, Name = c.Name, Color = c.Color, SortOrder = c.SortOrder, Count = count });
            }
        }
        CategorySidebar.ItemsSource = sidebarItems;
    }

    /// <summary>
    /// 创建一个分类折叠组（Expander）：头为"色块+分类名+数量"，
    /// 内容为该分类下的应用行列表；非搜索模式下展开一个自动收起其他（手风琴）。
    /// </summary>
    /// <param name="cat">分类数据</param>
    /// <param name="rules">该分类下的规则行</param>
    /// <param name="forceExpand">搜索模式强制展开</param>
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
        var itemsPanel = new StackPanel(); // 组内容容器：纵向堆叠应用行
        foreach (var rule in rules)
        {
            var row = CreateAppRow(rule); // 逐条构建应用行
            itemsPanel.Children.Add(row);
        }
        expander.Content = itemsPanel;

        return expander;
    }

    /// <summary>
    /// 构建分组头：横向排列 色块(14px) + 加粗分类名 + 灰色数量文字。
    /// </summary>
    private StackPanel CreateCategoryHeader(CategoryItem cat, int count)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var colorBox = new Border // 分类颜色小方块
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(cat.ColorValue) // 解析分类色
        };
        header.Children.Add(colorBox);

        var name = new TextBlock // 分类名（加粗，供侧边栏联动时按字体识别）
        {
            Text = cat.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(name);

        // 尾部数量小字（灰色）
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
    /// 构建单条应用行：复选框 + 图标(无图标用灰块占位) + 友好名，
    /// Tag 存进程名，挂接多选与拖拽事件。
    /// </summary>
    private Border CreateAppRow(RuleItem rule)
    {
        var displayName = AppDisplayName.Get(rule.ProcessName); // 进程名 → 友好名
        var icon = IconExtractor.GetIcon(rule.ProcessName);     // 取进程图标（带缓存）

        // 行容器：底边细线分隔，Tag 携带进程名
        var row = new Border
        {
            Padding = new Thickness(4, 3, 4, 3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Tag = rule.ProcessName, // 存进程名供拖拽和选择用
        };

        // 行内横向排布：勾选框 + 图标 + 名称
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

        row.Child = panel; // 装配行内容

        // 拖拽支持
        row.MouseMove += AppRow_MouseMove;             // 按住左键移动 → 发起拖拽
        row.MouseLeftButtonDown += AppRow_MouseLeftButtonDown; // 记录最近点击项（为 Shift 范围选择预留）

        return row;
    }

    /// <summary>应用行复选框勾选变化：维护多选集合并刷新"退出选择"按钮可见性。</summary>
    private void AppCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string procName) // Tag 即进程名
        {
            if (cb.IsChecked == true) // 勾选 → 加入选择集合
                _selectedProcessNames.Add(procName);
            else // 取消 → 移出集合
                _selectedProcessNames.Remove(procName);
            UpdateSelectionMode(); // 更新按钮显隐
            MarkChanged();         // 勾选本身也视为更改（会随保存写库）
        }
    }

    /// <summary>应用行左键按下：仅记录最近点击的进程名（Shift 范围选择的预留钩子）。</summary>
    private void AppRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string procName)
        {
            // Shift 范围选择(待实现,需记录视觉顺序)
            _lastClickedProcess = procName;
        }
    }

    /// <summary>
    /// 应用行按住左键移动：发起拖拽。
    /// 若有多选则拖拽整个选中集合，否则只拖当前行。
    /// </summary>
    private void AppRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is Border border && border.Tag is string procName)
        {
            // 如果有选中项,拖拽选中的;否则拖拽当前项
            var toDrag = _selectedProcessNames.Count > 0 ? _selectedProcessNames.ToList() : new List<string> { procName };
            var data = new DataObject(); // 自定义数据对象承载 "ProcessNames" 列表
            data.SetData("ProcessNames", toDrag);            // 打包进程名列表
            DragDrop.DoDragDrop(border, data, DragDropEffects.Move); // 开始拖放循环（阻塞至松开）
        }
    }

    /// <summary>根据是否有选中项显示/隐藏"退出选择"按钮。</summary>
    private void UpdateSelectionMode()
    {
        bool hasSelection = _selectedProcessNames.Count > 0; // 是否存在勾选项
        BtnExitSelect.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"退出选择"按钮：清空多选状态并重建面板复位复选框。</summary>
    private void BtnExitSelect_Click(object sender, RoutedEventArgs e)
    {
        _selectedProcessNames.Clear();   // 清空选中集合
        _lastClickedProcess = null;      // 清空最近点击记录
        // 重建面板清除勾选状态
        BuildRulesPanel();
        UpdateSelectionMode();
    }

    /// <summary>
    /// 搜索框文本变化：300ms 防抖后重建规则面板（避免每敲一键都全量重排）。
    /// </summary>
    private void TxtRuleSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;          // 装载阶段不触发
        _searchDebounceTimer?.Stop();  // 重置防抖计时
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; // 懒创建
        _searchDebounceTimer.Tick -= SearchDebounce_Tick;
        _searchDebounceTimer.Tick += SearchDebounce_Tick; // 先减后加，保证只挂一个处理器
        _searchDebounceTimer.Start();
    }

    /// <summary>防抖到期：停止计时器并按当前关键词重建面板。</summary>
    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer!.Stop();
        BuildRulesPanel();
    }

    /// <summary>
    /// 侧边栏选中项变化：在右侧面板中找到同名分类分组并展开、滚动到可见。
    /// 通过头部中"加粗 TextBlock"的文本来识别分组（构建时的约定）。
    /// </summary>
    private void CategorySidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategorySidebar == null) return; // 装载期/未就绪忽略
        if (CategorySidebar.SelectedItem is CategoryItem cat)
        {
            // 点击左侧分类 → 展开右侧对应分组
            foreach (var child in RulesPanel.Children)
            {
                if (child is Expander exp && exp.Header is StackPanel header)
                {
                    // 找到分类名匹配的 Expander
                    var nameBlock = header.Children.OfType<TextBlock>().FirstOrDefault(t => t.FontWeight == FontWeights.Bold);
                    if (nameBlock?.Text == cat.Name) // 名字匹配即为目标分组
                    {
                        exp.IsExpanded = true;   // 展开
                        exp.BringIntoView();     // 滚动到可视区域
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 侧边栏 DragOver：校验拖拽数据格式，并高亮鼠标悬停的分类项（浅蓝背景）。
    /// </summary>
    private void CategorySidebar_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ProcessNames")) // 不是应用拖拽则拒绝
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move; // 允许移动效果

        // 高亮当前悬停的 ListBoxItem
        var dep = e.OriginalSource as DependencyObject;
        ListBoxItem? hoveredItem = null;
        while (dep != null && dep is not ListBoxItem) // 沿可视树向上找行容器
            dep = VisualTreeHelper.GetParent(dep);
        hoveredItem = dep as ListBoxItem;

        // 遍历各行容器：命中悬停行则高亮，其余复位
        foreach (var item in CategorySidebar.Items)
        {
            if (CategorySidebar.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
            {
                if (lbi == hoveredItem && lbi.DataContext is CategoryItem) // 悬停行高亮
                    lbi.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD)); // 浅蓝
                else
                    lbi.Background = Brushes.Transparent; // 其余恢复透明
            }
        }
        e.Handled = true;
    }

    /// <summary>侧边栏 DragEnter：仅当携带 ProcessNames 数据时允许移动效果。</summary>
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

    /// <summary>侧边栏 DragLeave：拖出时清除所有行高亮。</summary>
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

    /// <summary>
    /// 侧边栏 Drop：把拖入的进程（可多个）改到目标分类（仅改内存，保存时落库）。
    /// 目标分类取自 Drop 命中的行，未命中则退回当前选中行。
    /// </summary>
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

        if (!e.Data.GetDataPresent("ProcessNames")) return; // 格式不符直接返回
        var procNames = e.Data.GetData("ProcessNames") as List<string>; // 取出拖拽携带的进程名集合
        if (procNames == null || procNames.Count == 0) return;

        // 找到目标分类:从 Drop 位置取 ListBoxItem
        CategoryItem? targetCat = null;
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem) // 向上找命中行
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem lbiItem && lbiItem.DataContext is CategoryItem cat)
            targetCat = cat;                            // 命中行即目标
        else if (CategorySidebar.SelectedItem is CategoryItem selectedCat)
            targetCat = selectedCat;                    // 兜底用当前选中项

        if (targetCat == null) return; // 无法确定目标分类

        // 改分类
        int changed = 0; // 实际发生分类变更的条数
        foreach (var procName in procNames)
        {
            var rule = _allRules.FirstOrDefault(r => r.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase)); // 找对应规则
            if (rule != null && rule.CategoryName != targetCat.Name) // 已是目标分类则跳过
            {
                rule.CategoryName = targetCat.Name;
                rule.IsCustom = true;  // 手动改动后升级为自定义规则
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

    // 注意：当前 XAML 中没有任何按钮绑定 BtnDeleteCategory_Click，疑似遗留死代码入口
    /// <summary>
    /// 删除分类按钮：预置分类(Id ≤ MaxPresetCategoryId)禁止删除；
    /// 自定义分类只从网格集合移除，点"保存"才真正写库。
    /// </summary>
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

    /// <summary>分类网格内容变化（编辑/增删行）：装载期忽略，否则标记未保存更改。</summary>
    private void CategoriesGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MarkChanged();
    }

    /// <summary>统一脏标记入口：交由 CheckHasChanges 汇总判定并更新界面提示。</summary>
    private void MarkChanged()
    {
        if (_loading) return; // 装载阶段不算更改
        CheckHasChanges();
    }

}
