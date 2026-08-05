using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeActivity.Models;

/// <summary>
/// 使用统计列表的每行数据
/// </summary>
public class StatsRowItem
{
    /// <summary>是否勾选（高亮）</summary>
    public bool IsChecked { get; set; }

    /// <summary>应用图标（类别统计时为 null）</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>名称（应用名或类别名）</summary>
    public string Name { get; set; } = "";

    /// <summary>类别（应用统计才有，类别统计为空）</summary>
    public string Category { get; set; } = "";

    /// <summary>总时长秒数</summary>
    public int TotalSeconds { get; set; }

    /// <summary>占总活跃时长的百分比 0~100</summary>
    public double Percent { get; set; }

    /// <summary>占比条颜色</summary>
    public string BarColor { get; set; } = "#4A90D9";

    /// <summary>格式化时长文本</summary>
    public string DurationText => Helpers.TimeFormatHelper.Format(TotalSeconds);

    /// <summary>百分比文本</summary>
    public string PercentText => $"{Percent:F1}%";

    /// <summary>百分比是否太大需要放在有色部分上（>80%）</summary>
    public bool PercentOnBar => Percent > 80;

    /// <summary>有色部分宽度占比（0~1，用于 Grid ColumnDefinition）</summary>
    public double BarFillWidth => Percent / 100.0;

    /// <summary>透明部分宽度占比</summary>
    public double BarEmptyWidth => 1.0 - BarFillWidth;

    /// <summary>透明部分是否太窄放不下百分比文字</summary>
    public bool TextOnEmpty => !PercentOnBar;
}
