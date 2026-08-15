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
/// 概览条渲染器 — 负责下方固定的 0~24h 全天缩略色块和视口指示框。
/// 遵循单一职责原则：只管概览条绘制，不管数据加载。
/// </summary>
public class OverviewRenderer
{
    private readonly CategoryColorHelper _colorHelper;

    /// <summary>
    /// 颜色查找函数：(进程名, 类别名) => Color。
    /// 默认用分类颜色，MainWindow 可替换为应用颜色模式。
    /// </summary>
    public Func<string, string, Color> GetColorFunc { get; set; }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="colorHelper">分类颜色助手</param>
    public OverviewRenderer(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
        GetColorFunc = (proc, cat) => _colorHelper.GetColor(cat);
    }

    /// <summary>
    /// 绘制概览条：全天 0~24h 的缩略色块 + 当前视口的蓝色指示框
    /// </summary>
    /// <param name="canvas">目标画布</param>
    /// <param name="width">画布宽度（像素）</param>
    /// <param name="height">画布高度（像素）</param>
    /// <param name="activities">当天所有活动记录</param>
    /// <param name="viewStart">可见范围起始秒数（0~86399）</param>
    /// <param name="visibleSeconds">可见范围跨度秒数</param>
    public void Draw(Canvas canvas, double width, double height,
        List<ActivityRecord> activities, double viewStart, double visibleSeconds)
    {
        canvas.Children.Clear();
        canvas.Height = height;

        // 一天固定 86400 秒
        const double totalSeconds = 86400;

        // 画灰色背景条
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

        // 画每个活动的缩略色块（跳过空闲）
        foreach (var act in activities)
        {
            if (act.IsIdle) continue;
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double durSec = act.Duration;
            // 跨午夜活动：startSec 在 23:00 但持续时间跨到次日，色块会超出画布右边界
            // 裁剪：色块不超出 width
            double x = (startSec / totalSeconds) * width;
            double w = Math.Max(Math.Min((durSec / totalSeconds) * width, width - x), 1);

            var color = GetColorFunc(act.ProcessName, act.Category);
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

        // 画视口指示框（蓝色半透明框，表示当前时间轴看到的是哪一段）
        double viewX = (viewStart / totalSeconds) * width;
        double viewW = (visibleSeconds / totalSeconds) * width;
        // 防止视口框超出画布右边界
        if (viewX + viewW > width) viewW = Math.Max(width - viewX, 1);

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
    /// 绘制概览条下方的刻度文字（每 3 小时一个数字）
    /// </summary>
    /// <param name="canvas">刻度画布</param>
    /// <param name="width">画布宽度</param>
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
