// —— 导入：图标用的 ImageSource 及位图相关类型 ——
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeActivity.Models;

/// <summary>
/// 使用统计列表的每行数据
/// </summary>
/// <remarks>
/// 统计页列表的行模型：除原始数据外，还暴露一组计算属性供 XAML 直接绑定
/// （时长文本、百分比、双色条宽度等）。纯展示 POCO，不实现变更通知——
/// 统计结果变化时由页面整批重建行集合。
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
    /// <remarks>分母为所选时间范围内全部有效活跃秒数之和</remarks>
    public double Percent { get; set; }

    /// <summary>占比条颜色</summary>
    /// <remarks>十六进制串：应用统计=所属分类色，类别统计=默认蓝</remarks>
    public string BarColor { get; set; } = "#4A90D9";

    /// <summary>格式化时长文本</summary>
    /// <remarks>计算属性：读取时把 TotalSeconds 格式化为 "1h23m" 样式</remarks>
    public string DurationText => Helpers.TimeFormatHelper.Format(TotalSeconds);

    /// <summary>百分比文本</summary>
    /// <remarks>固定保留 1 位小数</remarks>
    public string PercentText => $"{Percent:F1}%";

    /// <summary>百分比是否太大需要放在有色部分上（>80%）</summary>
    /// <remarks>阈值 80%：此时透明区太窄放不下文字，改叠印在色条上</remarks>
    public bool PercentOnBar => Percent > 80;

    /// <summary>有色部分宽度占比（0~1，用于 Grid ColumnDefinition）</summary>
    /// <remarks>两颗 Star 列分别绑定 BarFillWidth/BarEmptyWidth 拼出比例条</remarks>
    public double BarFillWidth => Percent / 100.0;

    /// <summary>透明部分宽度占比</summary>
    /// <remarks>= 1 − BarFillWidth，与有色部分互补凑满整行</remarks>
    public double BarEmptyWidth => 1.0 - BarFillWidth;

    /// <summary>透明部分是否太窄放不下百分比文字</summary>
    /// <remarks>= !PercentOnBar，XAML 据此切换百分比文字的摆放位置</remarks>
    public bool TextOnEmpty => !PercentOnBar;
}
