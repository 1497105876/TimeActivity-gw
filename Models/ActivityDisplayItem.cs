namespace TimeActivity.Models;

/// <summary>
/// 活动列表绑定的显示模型
/// </summary>
public class ActivityDisplayItem
{
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime StartTime { get; set; }
    public string DurationText { get; set; } = "";
}
