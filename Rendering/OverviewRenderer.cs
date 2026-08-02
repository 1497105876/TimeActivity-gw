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
/// 概览条渲染器 — 负责下方固定 0-24h 概览条和视口指示框
/// 遵循 SRP：只管概览条绘制
/// </summary>
public class OverviewRenderer
{
    private readonly CategoryColorHelper _colorHelper;

    public OverviewRenderer(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
    }

    /// <summary>
    /// 绘制概览条（全天缩略色块 + 视口指示框）
    /// </summary>
    public void Draw(Canvas canvas, double width, double height,
        List<ActivityRecord> activities, double viewStart, double visibleSeconds)
    {
        canvas.Children.Clear();
        canvas.Height = height;

        const double totalSeconds = 86400;

        // 背景
        var bg = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            RadiusX = 3,
            RadiusY = 3
        };
        Canvas.SetLeft(bg, 0);
        Canvas.SetTop(bg, 0);
        canvas.Children.Add(bg);

        // 色块（全天缩略）
        foreach (var act in activities)
        {
            if (act.IsIdle) continue;
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double durSec = act.Duration;
            double x = (startSec / totalSeconds) * width;
            double w = Math.Max((durSec / totalSeconds) * width, 1);

            var color = _colorHelper.GetColor(act.Category);
            var block = new Rectangle
            {
                Width = w,
                Height = height,
                Fill = new SolidColorBrush(color),
                Opacity = 0.7
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, 0);
            canvas.Children.Add(block);
        }

        // 视口指示框
        double viewX = (viewStart / totalSeconds) * width;
        double viewW = (visibleSeconds / totalSeconds) * width;

        var viewport = new Border
        {
            Width = viewW,
            Height = height,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(30, 0x33, 0x99, 0xFF)),
            CornerRadius = new CornerRadius(2)
        };
        Panel.SetZIndex(viewport, 100);
        Canvas.SetLeft(viewport, viewX);
        Canvas.SetTop(viewport, 0);
        canvas.Children.Add(viewport);
    }

    /// <summary>
    /// 绘制概览条刻度（固定 3 小时一个）
    /// </summary>
    public void DrawScale(Canvas canvas, double width)
    {
        canvas.Children.Clear();
        canvas.Height = 14;

        const double totalSeconds = 86400;
        int intervalMinutes = 180;

        for (int m = 0; m <= 1440; m += intervalMinutes)
        {
            double sec = m * 60;
            double x = (sec / totalSeconds) * width;

            var text = new TextBlock
            {
                Text = $"{m / 60}",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
            };
            Canvas.SetLeft(text, x + 2);
            Canvas.SetTop(text, 0);
            canvas.Children.Add(text);
        }
    }
}
