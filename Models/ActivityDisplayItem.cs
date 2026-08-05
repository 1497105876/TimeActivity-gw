using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 活动列表绑定的显示模型
/// </summary>
public class ActivityDisplayItem
{
    public ImageSource? Icon { get; set; }
    public string ProcessName { get; set; } = "";
    public string DisplayName { get; set; } = ""; // 友好显示名（如"任务管理器"而非"taskmgr"）
    public string WindowTitle { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string DurationText { get; set; } = "";
}
