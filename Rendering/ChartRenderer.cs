using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeActivity.Helpers;

namespace TimeActivity.Rendering;

/// <summary>
/// 统计图表渲染器 — 负责类别占比条形图、每日趋势图、Top 应用列表的绘制。
/// 遵循单一职责原则：只管画图，不管数据加载和 UI 事件。
/// </summary>
public class ChartRenderer
{
    // 分类颜色助手，根据分类名拿到对应颜色
    private CategoryColorHelper _colorHelper;

    /// <summary>
    /// 构造函数，传入颜色助手
    /// </summary>
    /// <param name="colorHelper">分类颜色助手</param>
    public ChartRenderer(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
    }

    /// <summary>
    /// 更新颜色助手引用（设置保存后刷新颜色用）
    /// </summary>
    public void SetColorHelper(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
    }

    /// <summary>
    /// 绘制类别占比条形图：每个分类一行，左边名称、中间色条、右边时长和百分比
    /// </summary>
    /// <param name="panel">要填充的容器面板</param>
    /// <param name="data">分类名 → 总秒数的字典</param>
    /// <param name="totalSeconds">总活跃秒数，用于算百分比</param>
    public void DrawCategoryBars(Panel panel, Dictionary<string, int> data, int totalSeconds)
    {
        panel.Children.Clear();

        // 没数据时显示占位文字
        if (data.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无数据",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 12
            });
            return;
        }

        // 遍历每个分类，画一行：名称 + 色条 + 时长 + 百分比
        foreach (var kvp in data)
        {
            var color = _colorHelper.GetColor(kvp.Key);
            double pct = totalSeconds > 0 ? (double)kvp.Value / totalSeconds : 0;
            string durStr = TimeFormatHelper.Format(kvp.Value);

            // 一行用 Grid 布局，4 列：名称、色条、时长、百分比
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            var name = new TextBlock
            {
                Text = kvp.Key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var barBg = new Border
            {
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(barBg, 1);

            var barFill = new Border
            {
                Height = 18,
                Width = Math.Max(pct * 100, 2),
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            barBg.Child = barFill;
            row.Children.Add(barBg);

            var dur = new TextBlock
            {
                Text = durStr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 2);
            row.Children.Add(dur);

            var pctText = new TextBlock
            {
                Text = $"{pct * 100:F1}%", FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctText, 3);
            row.Children.Add(pctText);

            panel.Children.Add(row);
        }
    }

    /// <summary>
    /// 绘制每日趋势折线图：X 轴是日期，Y 轴是活跃时长
    /// </summary>
    /// <param name="canvas">画布</param>
    /// <param name="dailyData">日期(yyyy-MM-dd) → 秒数的字典</param>
    /// <param name="start">范围起始日期</param>
    /// <param name="end">范围结束日期</param>
    public void DrawTrendChart(Canvas canvas, Dictionary<string, int> dailyData, DateTime start, DateTime end)
    {
        canvas.Children.Clear();

        // 画布还没布局完时给个默认宽度
        double w = canvas.ActualWidth;
        if (w <= 0) w = 800;
        double h = canvas.Height;

        int days = (end - start).Days + 1;
        if (days <= 1) days = 1;

        // 找最大值作为 Y 轴上限，默认 1 小时
        int maxSec = dailyData.Values.Count > 0 ? dailyData.Values.Max() : 3600;
        if (maxSec <= 0) maxSec = 3600;

        // 画 4 条水平刻度线 + Y 轴标签
        for (int i = 0; i <= 4; i++)
        {
            double y = h - 16 - (h - 32) * i / 4.0;
            var line = new Line
            {
                X1 = 40, Y1 = y, X2 = w, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                StrokeThickness = 1
            };
            canvas.Children.Add(line);

            int hours = (int)(maxSec * i / 4.0 / 3600);
            var label = new TextBlock
            {
                Text = $"{hours}h", FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 6);
            canvas.Children.Add(label);
        }

        // 计算每天的坐标点
        double barW = (w - 48) / days;
        var points = new List<Point>();
        for (int i = 0; i < days; i++)
        {
            DateTime day = start.AddDays(i);
            string key = day.ToString("yyyy-MM-dd");
            int sec = dailyData.ContainsKey(key) ? dailyData[key] : 0;

            // 计算这天数据点的坐标
            double x = 40 + i * barW + barW / 2;
            double y = h - 16 - (sec > 0 ? (h - 32) * ((double)sec / maxSec) : 0);
            points.Add(new Point(x, y));

            // 柱子够宽时才显示日期标签，否则太挤
            if (barW >= 30)
            {
                var label = new TextBlock
                {
                    Text = day.ToString("MM-dd"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                };
                Canvas.SetLeft(label, x - 15);
                Canvas.SetTop(label, h - 14);
                canvas.Children.Add(label);
            }
        }

        // 画折线段
        if (points.Count > 1)
        {
            for (int i = 0; i < points.Count - 1; i++)
            {
                var line = new Line
                {
                    X1 = points[i].X, Y1 = points[i].Y,
                    X2 = points[i + 1].X, Y2 = points[i + 1].Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                    StrokeThickness = 2
                };
                canvas.Children.Add(line);
            }

            // 画每个数据点的圆点
            foreach (var p in points)
            {
                var dot = new Ellipse
                {
                    Width = 5, Height = 5,
                    Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9))
                };
                Canvas.SetLeft(dot, p.X - 2.5);
                Canvas.SetTop(dot, p.Y - 2.5);
                canvas.Children.Add(dot);
            }
        }
    }

    /// <summary>
    /// 绘制 Top 应用排行榜：按时长降序排列，最多显示 topN 个
    /// </summary>
    /// <param name="panel">容器面板</param>
    /// <param name="data">进程名 → 总秒数的字典（已排好序）</param>
    /// <param name="topN">最多显示多少个</param>
    public void DrawTopApps(Panel panel, Dictionary<string, int> data, int topN = 15)
    {
        panel.Children.Clear();

        // 没数据时显示占位文字
        if (data.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无数据",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 12
            });
            return;
        }

        // 第一个就是最大值，用于算相对占比
        int top = Math.Min(data.Count, topN);
        int maxSec = data.Values.First();

        int i = 0;
        foreach (var kvp in data.Take(top))
        {
            double pct = maxSec > 0 ? (double)kvp.Value / maxSec : 0;

            // 一行：排名 + 名称 + 色条 + 时长
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            var rank = new TextBlock
            {
                Text = $"{i + 1}", FontSize = 12, FontWeight = FontWeight.FromOpenTypeWeight(700),
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rank, 0);
            row.Children.Add(rank);

            var name = new TextBlock
            {
                Text = kvp.Key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var barBg = new Border
            {
                Height = 14,
                Background = new SolidColorBrush(Color.FromArgb(30, 0x4A, 0x90, 0xD9)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(barBg, 2);
            var barFill = new Border
            {
                Height = 14,
                Width = Math.Max(pct * 100, 2),
                Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            barBg.Child = barFill;
            row.Children.Add(barBg);

            var dur = new TextBlock
            {
                Text = TimeFormatHelper.Format(kvp.Value), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 3);
            row.Children.Add(dur);

            panel.Children.Add(row);
            i++;
        }
    }

    // FormatDuration 方法已移到 TimeFormatHelper 统一管理
}
