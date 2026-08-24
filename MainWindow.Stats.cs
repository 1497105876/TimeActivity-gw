using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TimeActivity.Data;
using TimeActivity.Helpers;
using TimeActivity.Models;
using TimeActivity.Rendering;
using TimeActivity.Services;

namespace TimeActivity;

// ============================================================================
// MainWindow.Stats.cs — 主窗口的"统计列表与显示模型"部分类
// 职责：
//   1) ActivityRecord → ActivityDisplayItem 的显示模型包装；
//   2) "使用占比"两个列表（应用/类别）的聚合计算与行 UI 构建；
//   3) 统计行复选框勾选 → 时间轴高亮集合维护；
//   4) 顶部当日活跃总时长的更新。
// ============================================================================
public partial class MainWindow
{
    /// <summary>
    /// 把一条活动记录包装成列表可绑定的显示模型（补齐图标/友好名/格式化时长）。
    /// </summary>
    /// <param name="a">数据库中的活动记录</param>
    /// <returns>可直接绑定到 ListView 的显示项</returns>
    private static ActivityDisplayItem CreateDisplayItem(ActivityRecord a)
    {
        return new ActivityDisplayItem
        {
            Id = a.Id,                                                // 数据库主键（用于去重）
            Icon = IconExtractor.GetIcon(a.ProcessName),              // 从 exe 提取并缓存的进程图标
            ProcessName = a.ProcessName,                              // 进程内部标识
            DisplayName = Services.AppDisplayName.Get(a.ProcessName), // 友好显示名（如"任务管理器"）
            WindowTitle = a.WindowTitle,                              // 窗口标题
            Category = a.Category,                                    // 分类名
            StartTime = a.StartTime,                                  // 开始时间
            EndTime = a.EndTime,                                      // 结束时间
            DurationText = TimeFormatHelper.Format(a.Duration)        // 预格式化时长文本
        };
    }

    /// <summary>
    /// 从统计行控件中提取 Tag 字符串（应用名或分类名），
    /// 兼容两种视觉树结构：直接命中 Border 或 Border 作为 ContentControl 的内容。
    /// </summary>
    private static string? GetTagFromStatsRow(object item)
    {
        // item 是 ListViewItem，里面包裹的是 Border（CreateStatsRow 返回的）
        if (item is System.Windows.DependencyObject d)
        {
            var border = FindChild<Border>(d);
            if (border?.Tag is string s)
                return s;
            // Border 可能直接就是 item 的 Content
            if (item is System.Windows.Controls.ContentControl cc && cc.Content is Border b && b.Tag is string s2)
                return s2;
        }
        return null;
    }

    /// <summary>
    /// 加载"使用统计"两个列表：应用维度与分类维度。
    /// 数据取自当日缓存（排除空闲），按总时长降序生成行控件。
    /// </summary>
    private void LoadStatsLists()
    {
        // 清空两个列表
        AppStatsList.Items.Clear();
        CategoryStatsList.Items.Clear();

        // 从缓存数据聚合（排除空闲时段）
        var activities = _cachedActivities.Where(a => !a.IsIdle).ToList();
        if (activities.Count == 0) return; // 当日无活动则保持空列表

        // 总活跃秒数（作为占比计算的分母）
        int totalSeconds = activities.Sum(a => a.Duration);

        // 应用统计：按进程名分组，按时长降序
        var appGroups = activities
            .GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToList();

        foreach (var g in appGroups) // 每个进程生成一行
        {
            int sec = g.Sum(a => a.Duration);                            // 该进程总时长
            double pct = totalSeconds > 0 ? sec * 100.0 / totalSeconds : 0; // 占比百分比
            string cat = g.First().Category;                             // 该进程当前归属分类（取任一条）
            var row = CreateStatsRow(false, g.Key, cat, sec, pct,        // 构建行 UI
                AppColorAllocator.GetOrAssign(g.Key),                    // 应用专属颜色（自动分配或自定义）
                IconExtractor.GetIcon(g.Key),                            // 进程图标
                Services.AppDisplayName.Get(g.Key));                     // 友好显示名
            AppStatsList.Items.Add(row);
        }

        // 类别统计：按分类名分组，按时长降序
        var catGroups = activities
            .GroupBy(a => a.Category)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToList();

        foreach (var g in catGroups) // 每个分类生成一行
        {
            int sec = g.Sum(a => a.Duration);                                // 该分类总时长
            double pct = totalSeconds > 0 ? sec * 100.0 / totalSeconds : 0;  // 占比
            string color = _categoryColors.TryGetValue(g.Key, out var c) ? c : "#7F8C8D"; // 分类色，缺省灰色
            var row = CreateStatsRow(true, g.Key, "", sec, pct, color, null, g.Key); // 类别行无图标、无类别列
            CategoryStatsList.Items.Add(row);
        }
    }

    /// <summary>
    /// 构建一行统计 UI：复选框 + (图标) + 名称 + (类别) + 占比条 + 时长。
    /// isCategory=true 时为分类行（无图标/类别列，列宽略有差异）。
    /// </summary>
    private Border CreateStatsRow(bool isCategory, string name, string category, int seconds, double pct, string barColor, ImageSource? icon, string displayName)
    {
        // 行容器：Tag 存名称供右键菜单提取；透明背景保证整行可命中
        var row = new Border { Padding = new Thickness(2), Margin = new Thickness(0, 1, 0, 1), Tag = name, Background = System.Windows.Media.Brushes.Transparent };
        // 用 Grid 布局一行：复选框 + 图标 + 名称 + 类别 + 占比条 + 时长
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });   // checkbox
        if (!isCategory)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });   // icon
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(isCategory ? 100 : 80) }); // name
        if (!isCategory)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });   // category
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // bar
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // duration

        int col = 0; // 当前列游标，随控件添加递增

        // 复选框（勾选后高亮对应时间轴色块）
        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Tag = name };
        if (isCategory) // 分类行挂分类勾选事件
        {
            cb.Checked += CatStatsRow_CheckChanged;
            cb.Unchecked += CatStatsRow_CheckChanged;
        }
        else // 应用行挂应用勾选事件
        {
            cb.Checked += AppStatsRow_CheckChanged;
            cb.Unchecked += AppStatsRow_CheckChanged;
        }
        Grid.SetColumn(cb, col++);
        grid.Children.Add(cb);

        // 图标（仅应用行有）
        if (!isCategory)
        {
            var img = new Image { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(img, col++);
            grid.Children.Add(img);
        }

        // 名称
        var nameTb = new TextBlock { Text = displayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(nameTb, col++);
        grid.Children.Add(nameTb);

        // 类别（仅应用行有）
        if (!isCategory)
        {
            var catTb = new TextBlock { Text = category, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11 };
            Grid.SetColumn(catTb, col++);
            grid.Children.Add(catTb);
        }

        // 占比条 — 用 Canvas 实现，固定宽度 120px
        const double BarWidth = 120;
        const double BarHeight = 14;
        var barCanvas = new Canvas { Width = BarWidth, Height = BarHeight, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

        // 外框（灰色边框）
        var barBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), BorderThickness = new Thickness(1), Height = BarHeight, CornerRadius = new CornerRadius(2) };
        Canvas.SetLeft(barBorder, 0); Canvas.SetTop(barBorder, 0);
        barCanvas.Children.Add(barBorder);

        // 有色部分（按百分比填充）
        // 填充宽 = 条宽 × 百分比
        double fillWidth = BarWidth * pct / 100.0;
        // 行颜色字符串解析为 Color
        var fillColor = CategoryColorHelper.ParseHex(barColor);
        var fillBorder = new Border { Background = new SolidColorBrush(fillColor), Height = BarHeight - 2, CornerRadius = new CornerRadius(2, 0, 0, 2) };
        Canvas.SetLeft(fillBorder, 1); Canvas.SetTop(fillBorder, 1);
        // 内缩 1px 避免盖住边框；Math.Max 下限保护
        fillBorder.Width = Math.Max(0, fillWidth - 1);
        barCanvas.Children.Add(fillBorder);

        // 百分比文字：>80% 放在有色部分上（白字），否则放在透明部分（黑字）
        string pctText = $"{pct:F1}%"; // 保留一位小数
        var pctTb = new TextBlock { Text = pctText, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        pctTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); // 预测量文字宽度
        double textWidth = pctTb.DesiredSize.Width;

        if (pct > 80)
        {
            // 放在有色部分上居中，白色字
            pctTb.Foreground = Brushes.White;
            Canvas.SetLeft(pctTb, Math.Max(1, (fillWidth - textWidth) / 2));
        }
        else
        {
            // 放在透明部分开头，黑色字
            pctTb.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            Canvas.SetLeft(pctTb, fillWidth + 2);
        }
        // 垂直居中于占比条
        Canvas.SetTop(pctTb, (BarHeight - pctTb.DesiredSize.Height) / 2);
        barCanvas.Children.Add(pctTb);

        Grid.SetColumn(barCanvas, col++);
        grid.Children.Add(barCanvas);

        // 时长
        var durTb = new TextBlock { Text = TimeFormatHelper.Format(seconds), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(durTb, col++);
        grid.Children.Add(durTb);

        // 将整行 Grid 挂入边框容器并返回
        row.Child = grid;
        return row;
    }

    /// <summary>
    /// 应用行复选框勾选变化：维护 _checkedApps 高亮集合并重绘时间轴。
    /// </summary>
    private void AppStatsRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string appName) // Tag 即应用名
        {
            if (cb.IsChecked == true) // 勾选 → 加入高亮集合
                _checkedApps.Add(appName);
            else // 取消 → 移出高亮集合
                _checkedApps.Remove(appName);
            DrawAll(); // 重绘以反映高亮
        }
    }

    /// <summary>
    /// 分类行复选框勾选变化：维护 _checkedCategories 高亮集合并重绘时间轴。
    /// </summary>
    private void CatStatsRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string catName) // Tag 即分类名
        {
            if (cb.IsChecked == true) // 勾选 → 加入集合
                _checkedCategories.Add(catName);
            else // 取消 → 移出集合
                _checkedCategories.Remove(catName);
            DrawAll(); // 重绘以反映高亮
        }
    }

    /// <summary>
    /// 更新顶部"今日/某日活跃总时长"文本（HH小时MM分钟格式，不含空闲）。
    /// </summary>
    private void UpdateTodayTotal()
    {
        var summary = ActivityRepository.GetCategorySummaryByDate(_currentDate); // 按分类汇总当日时长
        int totalSeconds = summary.Values.Sum();                                 // 各分类求和得总活跃秒数
        // 秒数转 TimeSpan 以便取小时/分钟部分
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        string label = _currentDate == DateTime.Today ? "今日活跃" : $"{_currentDate:MM-dd} 活跃"; // 浏览历史日时显示具体日期
        // 写入顶部文本（浏览历史日时前缀带具体日期）
        TodayTotalText.Text = $"{label}：{ts.Hours}h{ts.Minutes}m";
    }

}
