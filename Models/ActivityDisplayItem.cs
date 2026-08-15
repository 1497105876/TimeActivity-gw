using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 活动列表绑定的显示模型
/// </summary>
public class ActivityDisplayItem
{
    /// <summary>数据库主键，用于去重</summary>
    public long Id { get; set; }

    /// <summary>应用图标（从进程 exe 提取）</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>进程名（内部标识，如 "chrome"）</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>友好显示名（如 "任务管理器" 而非 "taskmgr"）</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>窗口标题</summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>分类名</summary>
    public string Category { get; set; } = "";

    /// <summary>开始时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>结束时间</summary>
    public DateTime EndTime { get; set; }

    /// <summary>格式化好的时长文本，如 "1h23m"</summary>
    public string DurationText { get; set; } = "";
}
