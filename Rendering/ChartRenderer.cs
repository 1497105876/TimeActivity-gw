// ============================================================================
// ChartRenderer.cs — 统计图表渲染器
// 职责：类别占比条形图(DrawCategoryBars)、每日趋势折线图(DrawTrendChart)、
//       Top 应用排行列表(DrawTopApps) 的纯绘制逻辑。
// 数据由 StatisticsPage 聚合后传入；颜色经 CategoryColorHelper 解析。
// ============================================================================
// —— 导入：基础类型/LINQ 聚合、WPF 控件·媒体·形状、本项目助手类 ——
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
/// <remarks>
/// 所有 Draw* 方法均为"清空容器 → 全量重建子元素"的重绘模式；
/// 传入的时长统一以秒（int）为单位，百分比基于调用方聚合出的总数计算。
/// </remarks>
public class ChartRenderer
{
    // 分类颜色助手，根据分类名拿到对应颜色
    /// <summary>分类颜色助手：按分类名查 WPF 颜色，供所有图表取色</summary>
    private CategoryColorHelper _colorHelper;

    /// <summary>
    /// 构造函数，传入颜色助手
    /// </summary>
    /// <param name="colorHelper">分类颜色助手</param>
    public ChartRenderer(CategoryColorHelper colorHelper)
    {
        // 只保存引用不拷贝对象；设置页改色后经 SetColorHelper 同步新实例
        _colorHelper = colorHelper;
    }

    /// <summary>
    /// 更新颜色助手引用（设置保存后刷新颜色用）
    /// </summary>
    public void SetColorHelper(CategoryColorHelper colorHelper)
    {
        // 直接替换引用即可，下一次绘制立即使用新配色（无需通知机制）
        _colorHelper = colorHelper;
    }

    // ==================== 冻结 Brush 缓存（2026-08-25 内存优化） ====================
    // 固定色静态冻结复用；动态色（分类色/半透明轨道）走实例缓存，避免重绘产生大量未冻结对象
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();

    private SolidColorBrush GetBrush(Color c)
    {
        // 命中缓存直接返回：同色画刷只建一次，重绘不再重复分配
        if (_brushCache.TryGetValue(c, out var b)) return b;
        var brush = new SolidColorBrush(c); // 未命中：按颜色值新建
        brush.Freeze(); // Freeze 冻结：内容锁定，WPF 才能跨线程安全共享
        _brushCache[c] = brush; // 登记进缓存，后续同色直接命中
        return brush;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        // 静态固定色专用构造：颜色在初始化期定死，建一次即冻结即可
        var brush = new SolidColorBrush(c); // 按颜色值创建画刷
        brush.Freeze(); // 冻结后交给静态字段长期持有
        return brush;
    }

    // 图表固定色（空态文字/次要文字/网格线/趋势线/排名条轨道）
    private static readonly SolidColorBrush EmptyTextBrush = Frozen(Color.FromRgb(0xAA, 0xAA, 0xAA)); // "暂无数据"等提示文字：中浅灰
    private static readonly SolidColorBrush GrayTextBrush = Frozen(Color.FromRgb(0x99, 0x99, 0x99)); // 百分比等弱化文字：中灰
    private static readonly SolidColorBrush GridLineBrush = Frozen(Color.FromArgb(40, 0, 0, 0)); // 折线图水平网格：纯黑但 alpha=40，淡到不抢主图
    private static readonly SolidColorBrush TrendLineBrush = Frozen(Color.FromRgb(0x4A, 0x90, 0xD9)); // 主题蓝：趋势折线/数据点/排行条实心段
    private static readonly SolidColorBrush TopBarTrackBrush = Frozen(Color.FromArgb(30, 0x4A, 0x90, 0xD9)); // 排行条底轨：主题蓝 alpha=30 的浅底

    // ======================================================================
    // 类别占比条形图
    // ======================================================================
    /// <summary>
    /// 绘制类别占比条形图：每个分类一行，左边名称、中间色条、右边时长和百分比
    /// </summary>
    /// <param name="panel">要填充的容器面板</param>
    /// <param name="data">分类名 → 总秒数的字典</param>
    /// <param name="totalSeconds">总活跃秒数，用于算百分比</param>
    public void DrawCategoryBars(Panel panel, Dictionary<string, int> data, int totalSeconds)
    {
        // 全量重绘第一步：清空上一轮的所有行
        panel.Children.Clear();

        // 没数据时显示占位文字
        if (data.Count == 0)
        {
            // 空态提示：灰色小字，避免面板留白让用户误以为出错
            panel.Children.Add(new TextBlock
            {
                Text = "暂无数据",
                Foreground = EmptyTextBrush, // 冻结静态画刷（2026-08-25）
                FontSize = 12
            });
            // 空数据处理完毕，不再往下布局
            return;
        }

        // 遍历每个分类，画一行：名称 + 色条 + 时长 + 百分比
        foreach (var kvp in data)
        {
            // 取该分类的主题色（未登记的分类由助手回退默认色）
            var color = _colorHelper.GetColor(kvp.Key);
            // 占比 = 分类秒数 ÷ 总秒数；总秒数为 0 时防除零取 0
            double pct = totalSeconds > 0 ? (double)kvp.Value / totalSeconds : 0;
            // 把秒数格式化成 "1h23m" 式的可读时长文本
            string durStr = TimeFormatHelper.Format(kvp.Value);

            // 一行用 Grid 布局，4 列：名称、色条、时长、百分比
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) }; // 底部留 6px，行与行之间不粘连
            // 列宽方案：名称定宽 70px｜色条 Star 吃满剩余｜时长 80px｜百分比 56px
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            // 第 0 列：分类名称，垂直居中
            var name = new TextBlock
            {
                Text = kvp.Key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            // 第 1 列：色条底轨——主题色加 alpha=30 的半透明背景当轨道
            var barBg = new Border
            {
                Height = 18, // 轨道高 18px（条形整体高度）
                Background = GetBrush(Color.FromArgb(30, color.R, color.G, color.B)), // 冻结画刷缓存（2026-08-25）
                CornerRadius = new CornerRadius(3), // 圆角 3px
                Margin = new Thickness(4, 0, 8, 0), // 左右留白 4/8px，不与相邻列贴边
                VerticalAlignment = VerticalAlignment.Center // 行内垂直居中
            };
            Grid.SetColumn(barBg, 1);

            // 底轨内部左对齐的实心填充条，宽度随占比动态设置
            var barFill = new Border
            {
                Height = 18, // 与轨道同高，才呈现"底轨包填充条"的双层圆角效果
                Background = GetBrush(color), // 冻结画刷缓存（2026-08-25）
                CornerRadius = new CornerRadius(3), // 圆角 3px，与轨道一致
                HorizontalAlignment = HorizontalAlignment.Left // 左对齐：从轨道左侧开始按占比伸长
            };
            // 按占比填充：star 列实际宽度确定后才准确，用 SizeChanged 随布局更新，
            // 避免原先"pct*100 当像素"在列宽≠100px 时条形失真
            // 最少保底 2px，占比极小时也保持可见
            barBg.SizeChanged += (_, _) => barFill.Width = Math.Max(barBg.ActualWidth * pct, 2);
            // 填充条嵌入底轨，形成同高双层圆角效果
            barBg.Child = barFill;
            row.Children.Add(barBg);

            // 第 2 列：格式化后的时长文本
            var dur = new TextBlock
            {
                Text = durStr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 2);
            row.Children.Add(dur);

            // 第 3 列：百分比文本（保留 1 位小数，灰色弱化显示）
            var pctText = new TextBlock
            {
                Text = $"{pct * 100:F1}%", FontSize = 12,
                Foreground = GrayTextBrush, // 冻结静态画刷（2026-08-25）
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctText, 3);
            row.Children.Add(pctText);

            // 一行组装完毕，挂入容器继续下一分类
            panel.Children.Add(row);
        }
    }

    // ======================================================================
    // 每日趋势折线图
    // ======================================================================
    /// <summary>
    /// 绘制每日趋势折线图：X 轴是日期，Y 轴是活跃时长
    /// </summary>
    /// <param name="canvas">画布</param>
    /// <param name="dailyData">日期(yyyy-MM-dd) → 秒数的字典</param>
    /// <param name="start">范围起始日期</param>
    /// <param name="end">范围结束日期</param>
    public void DrawTrendChart(Canvas canvas, Dictionary<string, int> dailyData, DateTime start, DateTime end)
    {
        // 全量重绘：清掉上一轮的刻度线/标签/折线
        canvas.Children.Clear();

        // 画布还没布局完时给个默认宽度
        double w = canvas.ActualWidth;
        // 首帧布局前 ActualWidth 为 0，退回 800px 保证能画
        if (w <= 0) w = 800;
        double h = canvas.Height;
        // Height 未显式赋值时为 NaN，同样退回默认 400px
        if (double.IsNaN(h) || h <= 0) h = 400;

        // 闭区间天数：start==end 也应算 1 天，故 +1
        int days = (end - start).Days + 1;
        // 防御倒置区间(end<start)，至少按 1 天绘制
        if (days <= 1) days = 1;

        // 找最大值作为 Y 轴上限，默认 1 小时
        int maxSec = dailyData.Values.Count > 0 ? dailyData.Values.Max() : 3600;
        // 数据全 0 时仍取 3600 兜底：避免除零，坐标也不致全部压在底线
        if (maxSec <= 0) maxSec = 3600;

        // 画 5 条水平网格线（i=0 底线、i=4 顶线）作为 Y 轴刻度参考，左侧附整小时标签
        for (int i = 0; i <= 4; i++)
        {
            // i=0 为底线、i=4 为顶线；绘图区上下各留 16px 内边距
            // y 从底部往上缩：h-16 是底线，再按 i/4 比例扣掉 (h-32) 的可画区高度
            double y = h - 16 - (h - 32) * i / 4.0;
            var line = new Line
            {
                X1 = 40, Y1 = y, X2 = w, Y2 = y, // 横线：从左侧 Y 标签区边缘(40)拉到画布右缘
                Stroke = GridLineBrush, // 冻结静态画刷（2026-08-25）
                StrokeThickness = 1 // 1px 细网格，保持低调
            };
            canvas.Children.Add(line);

            // 刻度线 i 对应的数值为 maxSec 的 i/4，向下取整到整小时作为该层的标签
            int hours = (int)(maxSec * i / 4.0 / 3600);
            var label = new TextBlock
            {
                Text = $"{hours}h", FontSize = 9, // 小时标签：数字 + h，字号 9px
                Foreground = EmptyTextBrush // 冻结静态画刷（2026-08-25）
            };
            // 标签贴最左缘；上移 6px 让文字与刻度线视觉齐平
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 6);
            canvas.Children.Add(label);
        }

        // 计算每天的坐标点
        // 每日槽位宽 = 总宽 − 左右留白合计 48px，均摊到每一天
        double barW = (w - 48) / days;
        var points = new List<Point>();
        // 逐天扫描补齐：缺数据的日期按 0 秒处理，保证 X 轴连续不断档
        for (int i = 0; i < days; i++)
        {
            DateTime day = start.AddDays(i);
            // 以 yyyy-MM-dd 为键查当天活跃秒数，缺失补 0
            string key = day.ToDateKey();
            int sec = dailyData.ContainsKey(key) ? dailyData[key] : 0;

            // 计算这天数据点的坐标
            // X = 左边距 40 + 天序×槽宽 + 半槽宽（点落在槽位正中）
            double x = 40 + i * barW + barW / 2;
            // Y = 底线位置 − 归一化高度；0 秒恰好压在底线上
            double y = h - 16 - (sec > 0 ? (h - 32) * ((double)sec / maxSec) : 0);
            points.Add(new Point(x, y));

            // 柱子够宽时才显示日期标签，否则太挤
            if (barW >= 30)
            {
                var label = new TextBlock
                {
                    Text = day.ToString("MM-dd"), // 只显示"月-日"，省下年份的宽度
                    FontSize = 9, // 日期标签字号 9px
                    Foreground = EmptyTextBrush // 冻结静态画刷（2026-08-25）
                };
                // 标签中心近似对准数据点（左移 15px），贴住画布底部
                Canvas.SetLeft(label, x - 15);
                Canvas.SetTop(label, h - 14);
                canvas.Children.Add(label);
            }
        }

        // 画折线段
        // 少于 2 个点无法连线（单点也不单独标记）
        if (points.Count > 1)
        {
            // 相邻两点逐段连线，共 点数−1 段
            for (int i = 0; i < points.Count - 1; i++)
            {
                var line = new Line
                {
                    X1 = points[i].X, Y1 = points[i].Y,
                    X2 = points[i + 1].X, Y2 = points[i + 1].Y,
                    Stroke = TrendLineBrush, // 冻结静态画刷（2026-08-25）
                    StrokeThickness = 2
                };
                canvas.Children.Add(line);
            }

            // 画每个数据点的圆点
            foreach (var p in points)
            {
                // 5×5 实心圆点，突出每个采样值的位置
                var dot = new Ellipse
                {
                    Width = 5, Height = 5,
                    Fill = TrendLineBrush // 冻结静态画刷（2026-08-25）
                };
                // 圆心精确对准数据点（偏移半径 2.5）
                Canvas.SetLeft(dot, p.X - 2.5);
                Canvas.SetTop(dot, p.Y - 2.5);
                canvas.Children.Add(dot);
            }
        }
    }

    // ======================================================================
    // Top 应用排行列表
    // ======================================================================
    /// <summary>
    /// 绘制 Top 应用排行榜：按时长降序排列，最多显示 topN 个
    /// </summary>
    /// <param name="panel">容器面板</param>
    /// <param name="data">进程名 → 总秒数的字典（已排好序）</param>
    /// <param name="topN">最多显示多少个</param>
    public void DrawTopApps(Panel panel, Dictionary<string, int> data, int topN = 15)
    {
        // 全量重绘：清掉旧榜单
        panel.Children.Clear();

        // 没数据时显示占位文字
        if (data.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无数据",
                Foreground = EmptyTextBrush, // 冻结静态画刷（2026-08-25）
                FontSize = 12
            });
            return;
        }

        // 数据已按时长降序传入：榜首即最大值，用作相对占比基准
        // 榜单项数封顶 topN，防止越界
        int top = Math.Min(data.Count, topN);
        // 榜首应用的秒数，作为其余条目条形长度的归一化分母
        int maxSec = data.Values.Max();

        // 名次计数器（显示为 1 起）
        int i = 0;
        foreach (var kvp in data.Take(top))
        {
            // 相对榜首的占比（0~1），榜首自身恒为 100%
            double pct = maxSec > 0 ? (double)kvp.Value / maxSec : 0;

            // 一行：排名 + 名称 + 色条 + 时长
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            // 列宽方案：排名 28px｜应用名 160px｜色条 Star｜时长 70px
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            // 第 0 列：名次数字，加粗灰显
            var rank = new TextBlock
            {
                Text = $"{i + 1}", FontSize = 12, FontWeight = FontWeight.FromOpenTypeWeight(700),
                Foreground = GrayTextBrush, // 冻结静态画刷（2026-08-25）
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rank, 0);
            row.Children.Add(rank);

            // 第 1 列：应用名，超宽时截断加省略号
            var name = new TextBlock
            {
                Text = kvp.Key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            // 第 2 列：条形底轨——固定主题蓝 alpha=30 的半透明轨道
            var barBg = new Border
            {
                Height = 14, // 轨道高 14px（比类别条 18px 矮，弱化排行条的存在感）
                Background = TopBarTrackBrush, // 冻结静态画刷（2026-08-25）
                CornerRadius = new CornerRadius(3), // 圆角 3px
                Margin = new Thickness(4, 0, 8, 0), // 左右留白 4/8px
                VerticalAlignment = VerticalAlignment.Center // 行内垂直居中
            };
            Grid.SetColumn(barBg, 2);
            // 轨道内左对齐的实心蓝条，长度代表相对占比
            var barFill = new Border
            {
                Height = 14, // 与轨道同高，形成内嵌圆角蓝条
                Background = TrendLineBrush, // 冻结静态画刷（2026-08-25）
                CornerRadius = new CornerRadius(3), // 圆角 3px，与轨道一致
                HorizontalAlignment = HorizontalAlignment.Left // 左对齐：从轨道左端起按相对占比伸长
            };
            // 按占比填充：star 列实际宽度确定后才准确，用 SizeChanged 随布局更新
            // 最少保底 2px，极短条目也保持可见
            barBg.SizeChanged += (_, _) => barFill.Width = Math.Max(barBg.ActualWidth * pct, 2);
            barBg.Child = barFill;
            row.Children.Add(barBg);

            // 第 3 列：格式化时长文本
            var dur = new TextBlock
            {
                Text = TimeFormatHelper.Format(kvp.Value), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 3);
            row.Children.Add(dur);

            // 本行完成，名次递增继续下一条
            panel.Children.Add(row);
            i++;
        }
    }

    // FormatDuration 方法已移到 TimeFormatHelper 统一管理
}
