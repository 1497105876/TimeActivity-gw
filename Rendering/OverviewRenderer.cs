// ============================================================================
// OverviewRenderer.cs — 24 小时概览条渲染器
// 职责：把全天(0~86400 秒)活动压缩绘制到细条上，叠加当前可视窗口指示框；
//       绘制整日小时刻度。交互(拖拽平移)由 MainWindow 处理。
// ============================================================================
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
        // 2026-08-23 三轮优化：按颜色分组进 StreamGeometry，每种颜色一个 Path，
        // 整天大数据量下不再创建数千个 Rectangle。
        var groups = new Dictionary<Color, StreamGeometry>();
        foreach (var act in activities)
        {
            if (act.IsIdle) continue;
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double durSec = act.Duration;
            double x = (startSec / totalSeconds) * width;
            double w = Math.Max(Math.Min((durSec / totalSeconds) * width, width - x), 1);

            var color = GetColorFunc(act.ProcessName, act.Category);
            if (!groups.TryGetValue(color, out var geo))
            {
                geo = new StreamGeometry { FillRule = FillRule.Nonzero };
                groups[color] = geo;
            }
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x, 0), true, true);
                ctx.LineTo(new Point(x + w, 0), true, false);
                ctx.LineTo(new Point(x + w, height), true, false);
                ctx.LineTo(new Point(x, height), true, false);
            }
        }
        foreach (var kv in groups)
        {
            var path = new Path
            {
                Data = kv.Value,
                Fill = new SolidColorBrush(kv.Key),
                Opacity = 0.7,
                StrokeThickness = 0
            };
            Canvas.SetLeft(path, 0);
            Canvas.SetTop(path, 0);
            canvas.Children.Add(path);
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
