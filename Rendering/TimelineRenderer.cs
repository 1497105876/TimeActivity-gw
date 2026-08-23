// ============================================================================
// TimelineRenderer.cs — 主时间轴渲染器
// 职责：按视口(_viewStartSeconds/_visibleSeconds)把活动区间绘制为彩色横块，
//       支持高亮集合（未勾选淡化）；绘制随缩放自适应的时间刻度。
// 颜色经 GetColorFunc 委托取色（按应用/按分类模式由主窗口注入）。
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
/// 时间轴渲染器 — 负责上方可缩放时间轴的色块绘制和刻度标注。
/// 遵循单一职责原则：只管画，不管数据加载和 UI 事件。
/// </summary>
public class TimelineRenderer
{
    private readonly CategoryColorHelper _colorHelper;

    /// <summary>
    /// 颜色查找函数：(进程名, 类别名) => Color。
    /// 默认按分类着色，MainWindow 可切换为按应用着色。
    /// </summary>
    public Func<string, string, Color> GetColorFunc { get; set; }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="colorHelper">分类颜色助手</param>
    public TimelineRenderer(CategoryColorHelper colorHelper)
    {
        _colorHelper = colorHelper;
        GetColorFunc = (proc, cat) => _colorHelper.GetColor(cat);
    }

    /// <summary>
    /// 绘制时间轴色块（不带高亮，兼容旧调用）
    /// </summary>
    public void DrawActivities(Canvas canvas, double width, double height,
        List<ActivityRecord> activities, double viewStart, double visibleSeconds)
    {
        DrawActivities(canvas, width, height, activities, viewStart, visibleSeconds, null, null);
    }

    /// <summary>
    /// 绘制时间轴色块（带高亮：选中某个应用/分类时，其余变暗）
    /// </summary>
    /// <param name="canvas">目标画布</param>
    /// <param name="width">画布宽度</param>
    /// <param name="height">画布高度</param>
    /// <param name="activities">当天所有活动记录</param>
    /// <param name="viewStart">可见范围起始秒数</param>
    /// <param name="visibleSeconds">可见范围跨度秒数</param>
    /// <param name="highlightedApps">要高亮的应用名集合（null 表示不高亮）</param>
    /// <param name="highlightedCategories">要高亮的分类名集合（null 表示不高亮）</param>
    public void DrawActivities(Canvas canvas, double width, double height,
        List<ActivityRecord> activities, double viewStart, double visibleSeconds,
        HashSet<string>? highlightedApps, HashSet<string>? highlightedCategories)
    {
        canvas.Children.Clear();
        canvas.Height = height;

        // 画浅灰色背景
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

        bool hasHighlight = (highlightedApps != null && highlightedApps.Count > 0) ||
                            (highlightedCategories != null && highlightedCategories.Count > 0);

        // —— 高亮灰条（2026-08-23 新增）：选中应用/分类的时段画一条贯穿上下的灰色半透明竖带，
        //    即使色块本身很细也能一眼看到选中内容在时间轴上的分布位置 ——
        if (hasHighlight)
        {
            // 第一遍：收集选中段在可见范围内的像素区间 [x, x+w]
            var bands = new List<(double X, double W)>();
            foreach (var act in activities)
            {
                if (act.IsIdle) continue;
                bool sel = (highlightedApps != null && highlightedApps.Contains(act.ProcessName)) ||
                           (highlightedCategories != null && highlightedCategories.Contains(act.Category));
                if (!sel) continue;

                double startSec = act.StartTime.TimeOfDay.TotalSeconds;
                double endSec = act.EndTime.TimeOfDay.TotalSeconds;
                if (endSec < startSec) endSec += 86400;               // 跨午夜修正
                if (endSec <= viewStart || startSec >= viewStart + visibleSeconds) continue;

                double clipStart = Math.Max(startSec, viewStart);
                double clipEnd = Math.Min(endSec, viewStart + visibleSeconds);
                double x = ((clipStart - viewStart) / visibleSeconds) * width;
                double w = Math.Max((clipEnd - clipStart) / visibleSeconds * width, 1.5);
                bands.Add((x, w));
            }
            // 合并相邻/重叠区间，减少元素数量并避免接缝闪烁
            bands.Sort((a, b) => a.X.CompareTo(b.X));
            var merged = new List<(double X, double W)>();
            foreach (var b in bands)
            {
                if (merged.Count > 0 && b.X <= merged[^1].X + merged[^1].W + 1.5)
                {
                    var last = merged[^1];
                    double rightEnd = Math.Max(last.X + last.W, b.X + b.W);
                    merged[^1] = (last.X, rightEnd - last.X);
                }
                else merged.Add(b);
            }
            int bz = 0;
            foreach (var (x, w) in merged)
            {
                var band = new Rectangle
                {
                    Width = w,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromArgb(70, 0x60, 0x60, 0x60)), // 灰色半透明贯穿带
                    RadiusX = 2,
                    RadiusY = 2
                };
                Panel.SetZIndex(band, ++bz); // 位于背景之上、彩色块之下
                Canvas.SetLeft(band, x - 1); // 左右各扩 1px，让窄块也有可视宽度
                Canvas.SetTop(band, 0);
                canvas.Children.Add(band);
            }
        }

        // 遍历活动记录，画可见范围内的色块
        // 2026-08-23 性能优化：相邻且同色(含同样高亮状态)的段合并为一个矩形，
        // 整天数据时元素数从数千降到几十，重绘成本大幅下降。
        int z = 50; // 彩色块统一压在灰条之上
        Rectangle? run = null;      // 当前正在累积的同色段矩形
        double runX = 0, runW = 0;  // 累积段的位置与宽度
        Color runColor = default;   // 累积段颜色
        bool runDim = false;        // 累积段是否处于淡化状态

        foreach (var act in activities)
        {
            if (act.IsIdle) continue;

            // 把活动时间转成秒数
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double endSec = act.EndTime.TimeOfDay.TotalSeconds;
            // 跨午夜活动（如 23:50→00:10），endSec 会小于 startSec，加一天秒数
            if (endSec < startSec) endSec += 86400;

            // 不在可见范围内就跳过
            if (endSec <= viewStart || startSec >= viewStart + visibleSeconds)
                continue;

            // 裁剪到可见范围边界
            double clipStart = Math.Max(startSec, viewStart);
            double clipEnd = Math.Min(endSec, viewStart + visibleSeconds);
            double durSec = clipEnd - clipStart;

            // 秒数 → 像素坐标
            double x = ((clipStart - viewStart) / visibleSeconds) * width;
            double w = Math.Max((durSec / visibleSeconds) * width, 2);

            var color = GetColorFunc(act.ProcessName, act.Category);

            // 高亮逻辑：hasHighlight 已在灰条阶段计算；未选中的色块淡化（透明度 0.2）
            bool isHighlighted = false;
            if (hasHighlight)
            {
                isHighlighted = (highlightedApps != null && highlightedApps.Contains(act.ProcessName)) ||
                                (highlightedCategories != null && highlightedCategories.Contains(act.Category));
            }
            bool dim = hasHighlight && !isHighlighted;

            // 判断能否并入当前累积段：颜色相同、淡化状态相同、且与上一段首尾相接（容差 0.75px）
            if (run != null && runColor == color && runDim == dim &&
                Math.Abs((runX + runW) - x) <= 0.75)
            {
                double rightEnd = x + w;
                runW = Math.Max(runW, rightEnd - runX); // 扩展宽度
                continue;
            }

            // 无法并入 → 先把已累积的段落盘
            if (run != null)
            {
                run.Width = runW;
                Panel.SetZIndex(run, z++);
                Canvas.SetLeft(run, runX);
                Canvas.SetTop(run, 0);
                canvas.Children.Add(run);
            }

            // 开启新的累积段
            run = new Rectangle { Height = height, Fill = new SolidColorBrush(color), RadiusX = 1, RadiusY = 1 };
            run.Opacity = dim ? 0.2 : 1.0;
            runX = x;
            runW = w;
            runColor = color;
            runDim = dim;
        }

        // 收尾：把最后一段累积落盘
        if (run != null)
        {
            run.Width = runW;
            Panel.SetZIndex(run, z++);
            Canvas.SetLeft(run, runX);
            Canvas.SetTop(run, 0);
            canvas.Children.Add(run);
        }
    }

    /// <summary>
    /// 绘制时间轴上方的时刻刻度（自适应间隔）
    /// </summary>
    /// <param name="canvas">刻度画布</param>
    /// <param name="width">画布宽度</param>
    /// <param name="viewStart">可见范围起始秒数</param>
    /// <param name="visibleSeconds">可见范围跨度秒数</param>
    public void DrawScale(Canvas canvas, double width,
        double viewStart, double visibleSeconds)
    {
        canvas.Children.Clear();
        canvas.Height = 18;

        // 每像素代表多少秒，用于决定刻度密度
        double spp = visibleSeconds / width;
        double minIntervalSeconds = spp * 60; // 至少隔 60px 才标一个刻度

        // 根据缩放级别选合适的刻度间隔
        int intervalMinutes = ChooseInterval(minIntervalSeconds);
        double startMinutes = (int)(viewStart / 60 / intervalMinutes) * intervalMinutes;

        // 从可见范围起点开始标刻度
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
    /// 刻度间隔自适应算法：根据最小间隔要求选择 1/2/5/10/15/30/60/120... 分钟
    /// </summary>
    /// <param name="minIntervalSeconds">最小允许的刻度间隔（秒）</param>
    /// <returns>实际使用的刻度间隔（分钟）</returns>
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

    // FormatDuration 方法已移到 TimeFormatHelper 统一管理
}
