// —— 导入：图标用的 ImageSource 及位图相关类型 ——
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeActivity.Models;

/// <summary>
/// 使用统计列表的行数据模型
/// </summary>
/// <remarks>
/// 提供一条统计行所需的原始数据与派生展示值（时长文本、百分比、比例条宽等）。
/// 注意：当前统计列表实际由 MainWindow.Stats.CreateStatsRow 用代码逐行构建
/// Border/Grid 行控件（自绘 Canvas 比例条），并未绑定本模型；本类保留为
/// "模板化行数据"的定义参考，字段语义与 CreateStatsRow 的本地计算保持一致。
/// </remarks>
public class StatsRowItem
{
    /// <summary>是否勾选（高亮）</summary>
    /// <remarks>勾选后时间轴联动高亮该应用/分类的时段</remarks>
    public bool IsChecked { get; set; }

    /// <summary>应用图标（类别统计时为 null）</summary>
    /// <remarks>类别统计行没有对应单一进程，故无图标</remarks>
    public ImageSource? Icon { get; set; }

    /// <summary>名称（应用名或类别名）</summary>
    public string Name { get; set; } = "";

    /// <summary>类别（应用统计才有，类别统计为空）</summary>
    /// <remarks>冗余展示字段，便于在应用榜里直接看到归属分类</remarks>
    public string Category { get; set; } = "";

    /// <summary>总时长秒数</summary>
    /// <remarks>单位：秒；DurationText 由它即时格式化而来</remarks>
    public int TotalSeconds { get; set; }

    /// <summary>占总活跃时长的百分比 0~100</summary>
    /// <remarks>分母为统计范围内全部有效（非空闲）活跃秒数之和</remarks>
    public double Percent { get; set; }

    /// <summary>占比条颜色</summary>
    /// <remarks>十六进制串；类别统计默认蓝 #4A90D9，实际色值由调用方按行类型指定</remarks>
    public string BarColor { get; set; } = "#4A90D9";

    /// <summary>格式化时长文本</summary>
    /// <remarks>计算属性：读取时把 TotalSeconds 格式化为 "1h23m" 样式</remarks>
    public string DurationText => Helpers.TimeFormatHelper.Format(TotalSeconds);

    /// <summary>百分比文本</summary>
    /// <remarks>固定保留 1 位小数</remarks>
    public string PercentText => $"{Percent:F1}%";

    /// <summary>百分比是否过大、文字要叠放在有色部分上（>80%）</summary>
    /// <remarks>阈值 80%：此时空余区太窄放不下文字，改叠印在色条上</remarks>
    public bool PercentOnBar => Percent > 80;

    /// <summary>有色部分宽度占比（0~1）</summary>
    /// <remarks>= Percent/100；与 BarEmptyWidth 互补，可拼出宽度按比例的两段</remarks>
    public double BarFillWidth => Percent / 100.0;

    /// <summary>空余（透明）部分宽度占比</summary>
    /// <remarks>= 1 − BarFillWidth，与有色部分互补凑满整行</remarks>
    public double BarEmptyWidth => 1.0 - BarFillWidth;

    /// <summary>百分比文字是否应摆放在空余部分上（空余区够宽时为 true）</summary>
    /// <remarks>= !PercentOnBar，与 PercentOnBar 恰好互补</remarks>
    public bool TextOnEmpty => !PercentOnBar;
}
