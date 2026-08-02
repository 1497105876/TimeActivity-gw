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
/// 统计图表渲染器 — 类别占比条形图、每日趋势图、Top 应用
/// 遵循 SRP：只管图表绘制，不管数据加载和 UI 事件
/// </summary>
public class ChartRenderer
{
    private readonly CategoryColorHelper _colorHelper;

    public ChartRenderer(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
    }

    /// <summary>
    /// 绘制类别占比条形图
    /// </summary>
    public void DrawCategoryBars(Panel panel, Dictionary<string, int> data, int totalSeconds)
    {
        panel.Children.Clear();

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

        foreach (var kvp in data)
        {
            var color = _colorHelper.GetColor(kvp.Key);
            double pct = totalSeconds > 0 ? (double)kvp.Value / totalSeconds : 0;
            string durStr = FormatDuration(kvp.Value);

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
    /// 绘制每日趋势柱状图
    /// </summary>
    public void DrawTrendChart(Canvas canvas, Dictionary<string, int> dailyData, DateTime start, DateTime end)
    {
        canvas.Children.Clear();

        double w = canvas.ActualWidth;
        if (w <= 0) w = 800;
        double h = canvas.Height;

        int days = (end - start).Days + 1;
        if (days <= 1) days = 1;

        int maxSec = dailyData.Values.Count > 0 ? dailyData.Values.Max() : 3600;
        if (maxSec <= 0) maxSec = 3600;

        // 背景刻度线
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

        // 柱状图
        double barW = (w - 48) / days;
        for (int i = 0; i < days; i++)
        {
            DateTime day = start.AddDays(i);
            string key = day.ToString("yyyy-MM-dd");
            int sec = dailyData.ContainsKey(key) ? dailyData[key] : 0;

            double x = 40 + i * barW + 2;
            double barH = sec > 0 ? (h - 32) * ((double)sec / maxSec) : 0;
            double y = h - 16 - barH;

            if (sec > 0)
            {
                var bar = new Rectangle
                {
                    Width = Math.Max(barW - 4, 2),
                    Height = barH,
                    Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                    RadiusX = 2, RadiusY = 2
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, y);
                canvas.Children.Add(bar);
            }

            if (barW >= 30)
            {
                var label = new TextBlock
                {
                    Text = day.ToString("MM-dd"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, h - 14);
                canvas.Children.Add(label);
            }
        }
    }

    /// <summary>
    /// 绘制 Top 应用列表
    /// </summary>
    public void DrawTopApps(Panel panel, Dictionary<string, int> data, int topN = 15)
    {
        panel.Children.Clear();

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

        int top = Math.Min(data.Count, topN);
        int maxSec = data.Values.First();

        int i = 0;
        foreach (var kvp in data.Take(top))
        {
            double pct = maxSec > 0 ? (double)kvp.Value / maxSec : 0;

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
                Text = FormatDuration(kvp.Value), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 3);
            row.Children.Add(dur);

            panel.Children.Add(row);
            i++;
        }
    }

    /// <summary>
    /// 格式化时长显示
    /// </summary>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m";
        return $"{seconds / 3600}h{(seconds % 3600) / 60}m";
    }
}
