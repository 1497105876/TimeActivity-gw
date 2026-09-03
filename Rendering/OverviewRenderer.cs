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

    // ==================== 冻结 Brush 缓存（2026-08-25 内存优化） ====================
    // 与 TimelineRenderer 同策略：固定色静态冻结 + 动态色实例缓存，降低重绘分配
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();

    // 动态取色→画刷的统一入口：首次遇到某颜色才新建并 Freeze，此后同色直接复用
    private SolidColorBrush GetBrush(Color c)
    {
        // 命中缓存直接返回，避免重绘时反复创建画刷对象
        if (_brushCache.TryGetValue(c, out var b)) return b;
        var brush = new SolidColorBrush(c); // 未命中：按颜色值新建一个画刷
        brush.Freeze(); // Freeze 冻结：内容锁定，WPF 才能跨线程安全共享
        _brushCache[c] = brush; // 登记进缓存，后续同色直接命中
        return brush;
    }

    // 静态固定色的专用构造：初始化期一次性建立，用后即冻结，无需走缓存
    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c); // 按颜色值创建画刷
        brush.Freeze(); // 固定色内容不可变，冻结后交给静态字段持有
        return brush;
    }

    // 概览条固定色（背景/视口框/刻度文字）
    private static readonly SolidColorBrush BgBrush = Frozen(Color.FromRgb(0xE8, 0xE8, 0xE8)); // 无活动段的底色：浅灰
    private static readonly SolidColorBrush ViewportBorderBrush = Frozen(Color.FromRgb(0x33, 0x99, 0xFF)); // 视口框描边：亮蓝
    private static readonly SolidColorBrush ViewportFillBrush = Frozen(Color.FromArgb(30, 0x33, 0x99, 0xFF)); // 视口框内部填充：同蓝但 alpha=30，近似透明
    private static readonly SolidColorBrush ScaleTextBrush = Frozen(Color.FromRgb(0xAA, 0xAA, 0xAA)); // 刻度数字文字：中浅灰

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
            Width = width, // 横向铺满整条概览
            Height = height, // 纵向撑满（主窗口固定传 20px）
            Fill = BgBrush, // 冻结静态画刷（2026-08-25）
            RadiusX = 3, // 圆角 3px：视觉上更柔和
            RadiusY = 3 // 与 RadiusX 配对，四个角都带圆角
        };
        // 定位到画布左上角
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
                    // 每个矩形按顺时针描 4 个顶点：左上→右上→右下→左下，由闭合标志收口
                    ctx.BeginFigure(new Point(x, 0), true, true); // 起点取左上角 (x,0)
                    ctx.LineTo(new Point(x + w, 0), true, false); // 顶边：向右走 w 到右上角
                    ctx.LineTo(new Point(x + w, height), true, false); // 右边：下探到画布底部
                    ctx.LineTo(new Point(x, height), true, false); // 底边：向左回到左下角
                }
            }
            // 整体透明度 0.7 让底色微透，层次更好
            var path = new Path
            {
                Data = geo, // 前面拼好的 StreamGeometry 作为形状数据
                Fill = GetBrush(kv.Key), // 冻结画刷缓存（2026-08-25）
                Opacity = 0.7, // 0.7 不透明度：让灰底透出来，区分出空闲段
                StrokeThickness = 0 // 无描边，只显示填充色块
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
            Width = viewW, // 视口宽（秒→像素）：越大表示当前看得越宽
            Height = height, // 与概览条同高，覆盖整条
            BorderBrush = ViewportBorderBrush, // 冻结静态画刷（2026-08-25）
            BorderThickness = new Thickness(2), // 四边各 2px 描边
            Background = ViewportFillBrush, // 框内淡蓝填充
            CornerRadius = new CornerRadius(2) // 描边圆角 2px
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
                Foreground = ScaleTextBrush // 冻结静态画刷（2026-08-25）
            };
            // 右移 2px 微调，让数字视觉上对准刻度位置
            Canvas.SetLeft(text, x + 2);
            Canvas.SetTop(text, 0);
            canvas.Children.Add(text);
        }
    }
}
