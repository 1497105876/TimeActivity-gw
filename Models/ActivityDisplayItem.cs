using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 活动列表绑定的显示模型。实现 INotifyPropertyChanged，
/// 这样自动刷新改了已存在条目的"时长/分类"时，ListView 能立刻反映出来。
/// </summary>
public class ActivityDisplayItem : INotifyPropertyChanged
{
    /// <summary>数据库主键，用于去重</summary>
    public long Id { get; set; }

    private ImageSource? _icon;
    /// <summary>应用图标（从进程 exe 提取）</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { if (!Equals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
    }

    /// <summary>进程名（内部标识，如 "chrome"）</summary>
    public string ProcessName { get; set; } = "";

    private string _displayName = "";
    /// <summary>友好显示名（如 "任务管理器" 而非 "taskmgr"）</summary>
    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    private string _windowTitle = "";
    /// <summary>窗口标题</summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set { if (_windowTitle != value) { _windowTitle = value; OnPropertyChanged(); } }
    }

    private string _category = "";
    /// <summary>分类名</summary>
    public string Category
    {
        get => _category;
        set { if (_category != value) { _category = value; OnPropertyChanged(); } }
    }

    /// <summary>开始时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>结束时间</summary>
    public DateTime EndTime { get; set; }

    private string _durationText = "";
    /// <summary>格式化好的时长文本，如 "1h23m"</summary>
    public string DurationText
    {
        get => _durationText;
        set { if (_durationText != value) { _durationText = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName!));
}
