using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeActivity.Helpers;
using TimeActivity.Models;

namespace TimeActivity.Rendering;

/// <summary>
/// 时间轴渲染器 — 负责上方可缩放时间轴的色块和刻度绘制
/// 遵循 SRP：只管时间轴绘制，不管数据加载和 UI 事件
/// </summary>
public class TimelineRenderer
{
    private readonly CategoryColorHelper _colorHelper;

    public TimelineRenderer(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
    }

    /// <summary>
    /// 绘制主时间轴色块
    /// </summary>
    public void DrawActivities(Canvas canvas, double width, double height,
        List<ActivityRecord> activities, double viewStart, double visibleSeconds)
    {
        canvas.Children.Clear();
        canvas.Height = height;

        // 背景
        var bg = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            RadiusX = 4,
            RadiusY = 4
        };
        Panel.SetZIndex(bg, 0);
        Canvas.SetLeft(bg, 0);
        Canvas.SetTop(bg, 0);
        canvas.Children.Add(bg);

        // 色块 — 只画可见范围内的
        int z = 1;
        foreach (var act in activities)
        {
            if (act.IsIdle) continue;

            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double endSec = act.EndTime.TimeOfDay.TotalSeconds;

            if (endSec <= viewStart || startSec >= viewStart + visibleSeconds)
                continue;

            double clipStart = Math.Max(startSec, viewStart);
            double clipEnd = Math.Min(endSec, viewStart + visibleSeconds);
            double durSec = clipEnd - clipStart;

            double x = ((clipStart - viewStart) / visibleSeconds) * width;
            double w = Math.Max((durSec / visibleSeconds) * width, 2);

            var color = _colorHelper.GetColor(act.Category);
            var block = new Rectangle
            {
                Width = w,
                Height = height,
                Fill = new SolidColorBrush(color),
                Tag = act
            };
            Panel.SetZIndex(block, z++);
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, 0);
            canvas.Children.Add(block);
        }
    }

    /// <summary>
    /// 绘制上方时间刻度
    /// </summary>
    public void DrawScale(Canvas canvas, double width,
        double viewStart, double visibleSeconds)
    {
        canvas.Children.Clear();
        canvas.Height = 18;

        double spp = visibleSeconds / width;
        double minIntervalSeconds = spp * 60;

        int intervalMinutes = ChooseInterval(minIntervalSeconds);
        double startMinutes = (int)(viewStart / 60 / intervalMinutes) * intervalMinutes;

        for (int m = (int)startMinutes; m <= 1440; m += intervalMinutes)
        {
            double sec = m * 60;
            if (sec < viewStart) continue;
            if (sec > viewStart + visibleSeconds) break;

            double x = ((sec - viewStart) / visibleSeconds) * width;

            var line = new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = 6,
                Stroke = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                StrokeThickness = 1
            };
            canvas.Children.Add(line);

            int h = m / 60;
            int mm = m % 60;
            string label = mm == 0 ? $"{h}" : $"{h}:{mm:D2}";

            var text = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
            };
            Canvas.SetLeft(text, x + 2);
            Canvas.SetTop(text, 6);
            canvas.Children.Add(text);
        }
    }

    /// <summary>
    /// 刻度间隔自适应算法
    /// </summary>
    public static int ChooseInterval(double minIntervalSeconds)
    {
        if (minIntervalSeconds <= 60) return 1;
        if (minIntervalSeconds <= 2 * 60) return 2;
        if (minIntervalSeconds <= 5 * 60) return 5;
        if (minIntervalSeconds <= 10 * 60) return 10;
        if (minIntervalSeconds <= 15 * 60) return 15;
        if (minIntervalSeconds <= 30 * 60) return 30;
        if (minIntervalSeconds <= 60 * 60) return 60;
        if (minIntervalSeconds <= 2 * 3600) return 120;
        if (minIntervalSeconds <= 3 * 3600) return 180;
        if (minIntervalSeconds <= 4 * 3600) return 240;
        if (minIntervalSeconds <= 6 * 3600) return 360;
        return 720;
    }

    /// <summary>
    /// 格式化时长显示
    /// </summary>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m{seconds % 60}s";
        return $"{seconds / 3600}h{(seconds % 3600) / 60}m";
    }
}
