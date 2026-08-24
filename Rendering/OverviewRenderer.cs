// ============================================================================
// OverviewRenderer.cs — 24 小时概览条渲染器
// 职责：把全天(0~86400 秒)活动压缩绘制到细条上，叠加当前可视窗口指示框；
//       绘制整日小时刻度。交互(拖拽平移)由 MainWindow 处理。
// ============================================================================
// —— 导入：WPF 控件·媒体·形状、时间助手与活动记录模型 ——
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
/// <remarks>
/// 时间↔像素换算基准：一天恒为 86400 秒线性铺满画布宽，
/// 即任意时刻 t 对应像素 x = t / 86400 × width。
/// </remarks>
public class OverviewRenderer
{
    /// <summary>
    /// 分类颜色助手：构造时注入，供默认取色委托使用
    /// </summary>
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
        // 默认按分类着色；主窗口可在运行时整体替换此委托
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
        // 全量重绘：清空上一轮图形
        canvas.Children.Clear();
        // 与主条保持同一画布高度
        canvas.Height = height;

        // 一天固定 86400 秒
        const double totalSeconds = 86400;

        // 画灰色背景条
        // 圆角矩形铺满画布，充当无活动时段的底色
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
        // 2026-08-23 三轮优化：按颜色分组收集矩形，每组一个 Path（单 Open 会话，
        // 避免反复 Open 重置几何导致丢块）。整天大数据量下不再创建数千个 Rectangle。
        // 键=颜色，值=该色所有矩形的 (起点x, 宽度px) 像素区间
        var groups = new Dictionary<Color, List<(double X, double W)>>();
        foreach (var act in activities)
        {
            // 空闲时段不着色，保持背景灰
            if (act.IsIdle) continue;
            // 活动开始时刻 → 当天第几秒（0~86400）
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            // Duration 即持续秒数（int）
            double durSec = act.Duration;
            // 时间→像素线性映射：起点按全天等比压缩
            double x = (startSec / totalSeconds) * width;
            // 长度最短保 1px 保证可见；右端越界则截到画布边缘
            double w = Math.Max(Math.Min((durSec / totalSeconds) * width, width - x), 1);

            // 经委托取色（分类色/应用色模式由外部注入决定）
            var color = GetColorFunc(act.ProcessName, act.Category);
            // 同色归组，组内累积矩形区间
            if (!groups.TryGetValue(color, out var list))
            {
                list = new List<(double X, double W)>();
                groups[color] = list;
            }
            list.Add((x, w));
        }
        // 每种颜色一个 Path：把该色全部矩形写进同一 StreamGeometry
        foreach (var kv in groups)
        {
            // Nonzero 填充规则：重叠区块不会互相挖空
            var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
            // 关键：必须在单个 Open 会话里写完全部图形——
            // 再次 Open 会重置几何，此前内容全部丢失
            using (var ctx = geo.Open())
            {
                foreach (var (x, w) in kv.Value)
                {
                    // 逐个追加闭合的四点矩形轮廓
                    ctx.BeginFigure(new Point(x, 0), true, true);
                    ctx.LineTo(new Point(x + w, 0), true, false);
                    ctx.LineTo(new Point(x + w, height), true, false);
                    ctx.LineTo(new Point(x, height), true, false);
                }
            }
            // 整体透明度 0.7 让底色微透，层次更好
            var path = new Path
            {
                Data = geo,
                Fill = new SolidColorBrush(kv.Key),
                Opacity = 0.7,
                StrokeThickness = 0
            };
            Canvas.SetLeft(path, 0);
            Canvas.SetTop(path, 0);
            canvas.Children.Add(path);
        }

        // 画视口指示框（蓝色半透明框，表示当前时间轴看到的是哪一段）
        // 视口起止秒数同样按全天比例映射为像素
        double viewX = (viewStart / totalSeconds) * width;
        double viewW = (visibleSeconds / totalSeconds) * width;
        // 防止视口框超出画布右边界
        // 截断后至少保留 1px，避免框完全消失
        if (viewX + viewW > width) viewW = Math.Max(width - viewX, 1);

        // 蓝框：2px 描边 + alpha=30 浅蓝填充，标识当前查看的时间窗
        var viewport = new Border
        {
            Width = viewW,
            Height = height,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(30, 0x33, 0x99, 0xFF)),
            CornerRadius = new CornerRadius(2)
        };
        // ZIndex 拉到 100，确保盖在所有活动色块之上
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
        // 清空旧刻度重新绘制
        canvas.Children.Clear();
        // 刻度栏固定高 14px，与主窗口布局约定一致
        canvas.Height = 14;

        const double totalSeconds = 86400;
        // 每 180 分钟(3 小时)一格：全天标注 0、3、6 … 21、24 共 9 个数字
        int intervalMinutes = 180;

        // m 为分钟计数，从 0 扫到 1440（含两端）
        for (int m = 0; m <= 1440; m += intervalMinutes)
        {
            // 分钟→秒→像素：与主条使用同一套线性映射
            double sec = m * 60;
            double x = (sec / totalSeconds) * width;

            var text = new TextBlock
            {
                Text = $"{m / 60}",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
            };
            // 右移 2px 微调，让数字视觉上对准刻度位置
            Canvas.SetLeft(text, x + 2);
            Canvas.SetTop(text, 0);
            canvas.Children.Add(text);
        }
    }
}
