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

public partial class MainWindow
{
    private static ActivityDisplayItem CreateDisplayItem(ActivityRecord a)
    {
        return new ActivityDisplayItem
        {
            Id = a.Id,
            Icon = IconExtractor.GetIcon(a.ProcessName),
            ProcessName = a.ProcessName,
            DisplayName = Services.AppDisplayName.Get(a.ProcessName),
            WindowTitle = a.WindowTitle,
            Category = a.Category,
            StartTime = a.StartTime,
            EndTime = a.EndTime,
            DurationText = TimeFormatHelper.Format(a.Duration)
        };
    }

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

    private void LoadStatsLists()
    {
        // 清空两个列表
        AppStatsList.Items.Clear();
        CategoryStatsList.Items.Clear();

        // 从缓存数据聚合（排除空闲时段）
        var activities = _cachedActivities.Where(a => !a.IsIdle).ToList();
        if (activities.Count == 0) return;

        // 总活跃秒数
        int totalSeconds = activities.Sum(a => a.Duration);

        // 应用统计：按进程名分组，按时长降序
        var appGroups = activities
            .GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToList();

        foreach (var g in appGroups)
        {
            int sec = g.Sum(a => a.Duration);
            double pct = totalSeconds > 0 ? sec * 100.0 / totalSeconds : 0;
            string cat = g.First().Category;
            var row = CreateStatsRow(false, g.Key, cat, sec, pct,
                AppColorAllocator.GetOrAssign(g.Key),
                IconExtractor.GetIcon(g.Key),
                Services.AppDisplayName.Get(g.Key));
            AppStatsList.Items.Add(row);
        }

        // 类别统计：按分类名分组，按时长降序
        var catGroups = activities
            .GroupBy(a => a.Category)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToList();

        foreach (var g in catGroups)
        {
            int sec = g.Sum(a => a.Duration);
            double pct = totalSeconds > 0 ? sec * 100.0 / totalSeconds : 0;
            string color = _categoryColors.TryGetValue(g.Key, out var c) ? c : "#7F8C8D";
            var row = CreateStatsRow(true, g.Key, "", sec, pct, color, null, g.Key);
            CategoryStatsList.Items.Add(row);
        }
    }

    private Border CreateStatsRow(bool isCategory, string name, string category, int seconds, double pct, string barColor, ImageSource? icon, string displayName)
    {
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

        int col = 0;

        // 复选框（勾选后高亮对应时间轴色块）
        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Tag = name };
        if (isCategory)
        {
            cb.Checked += CatStatsRow_CheckChanged;
            cb.Unchecked += CatStatsRow_CheckChanged;
        }
        else
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
        double fillWidth = BarWidth * pct / 100.0;
        var fillColor = CategoryColorHelper.ParseHex(barColor);
        var fillBorder = new Border { Background = new SolidColorBrush(fillColor), Height = BarHeight - 2, CornerRadius = new CornerRadius(2, 0, 0, 2) };
        Canvas.SetLeft(fillBorder, 1); Canvas.SetTop(fillBorder, 1);
        fillBorder.Width = Math.Max(0, fillWidth - 1);
        barCanvas.Children.Add(fillBorder);

        // 百分比文字：>80% 放在有色部分上（白字），否则放在透明部分（黑字）
        string pctText = $"{pct:F1}%";
        var pctTb = new TextBlock { Text = pctText, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        pctTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
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
        Canvas.SetTop(pctTb, (BarHeight - pctTb.DesiredSize.Height) / 2);
        barCanvas.Children.Add(pctTb);

        Grid.SetColumn(barCanvas, col++);
        grid.Children.Add(barCanvas);

        // 时长
        var durTb = new TextBlock { Text = TimeFormatHelper.Format(seconds), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(durTb, col++);
        grid.Children.Add(durTb);

        row.Child = grid;
        return row;
    }

    private void AppStatsRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string appName)
        {
            if (cb.IsChecked == true)
                _checkedApps.Add(appName);
            else
                _checkedApps.Remove(appName);
            DrawAll();
        }
    }

    private void CatStatsRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string catName)
        {
            if (cb.IsChecked == true)
                _checkedCategories.Add(catName);
            else
                _checkedCategories.Remove(catName);
            DrawAll();
        }
    }

    private void UpdateTodayTotal()
    {
        var summary = ActivityRepository.GetCategorySummaryByDate(_currentDate);
        int totalSeconds = summary.Values.Sum();
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        string label = _currentDate == DateTime.Today ? "今日活跃" : $"{_currentDate:MM-dd} 活跃";
        TodayTotalText.Text = $"{label}：{ts.Hours}h{ts.Minutes}m";
    }

}
