// —— 导入：INPC 契约、CallerMemberName 特性、图标用的 ImageSource ——
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 活动列表绑定的显示模型。实现 INotifyPropertyChanged，
/// 这样自动刷新改了已存在条目的"时长/分类"时，ListView 能立刻反映出来。
/// </summary>
/// <remarks>
/// 通知策略：Icon/DisplayName/WindowTitle/Category/DurationText 走属性通知；
/// Id/ProcessName/StartTime/EndTime 属于一次性赋值的标识与原始时间，
/// 不触发通知（界面展示的是格式化后的 DurationText）。
/// </remarks>
public class ActivityDisplayItem : INotifyPropertyChanged
{
    /// <summary>数据库主键，用于去重</summary>
    /// <remarks>自动刷新时据此定位已存在的条目做原地更新，而非重复插入</remarks>
    public long Id { get; set; }

    private ImageSource? _icon;
    /// <summary>应用图标（从进程 exe 提取）</summary>
    /// <remarks>用 Equals 比较（可安全处理 null），仅真正变化才发通知，避免无谓重绘</remarks>
    public ImageSource? Icon
    {
        get => _icon;
        set { if (!Equals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
    }

    /// <summary>进程名（内部标识，如 "chrome"）</summary>
    /// <remarks>一次性赋值不发通知；对外展示用 DisplayName</remarks>
    public string ProcessName { get; set; } = "";

    private string _displayName = "";
    /// <summary>友好显示名（如 "任务管理器" 而非 "taskmgr"）</summary>
    /// <remarks>异步补全后回填此属性并通知刷新对应单元格</remarks>
    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    private string _windowTitle = "";
    /// <summary>窗口标题</summary>
    /// <remarks>实时跟随前台窗口变化，变化即通知 UI</remarks>
    public string WindowTitle
    {
        get => _windowTitle;
        set { if (_windowTitle != value) { _windowTitle = value; OnPropertyChanged(); } }
    }

    private string _category = "";
    /// <summary>分类名</summary>
    /// <remarks>规则变更后重算回写并通知刷新</remarks>
    public string Category
    {
        get => _category;
        set { if (_category != value) { _category = value; OnPropertyChanged(); } }
    }

    /// <summary>开始时间</summary>
    /// <remarks>一次性赋值，不触发通知</remarks>
    public DateTime StartTime { get; set; }

    private DateTime _endTime;
    /// <summary>结束时间</summary>
    /// <remarks>2026-09-02 修复：补 INPC 通知 —— XAML"结束"列绑定此属性，此前 60s tick 更新
    /// 进行中活动的 EndTime 时界面不刷新（检查报告 2.4/3.5）。StartTime 一次性赋值仍不通知。</remarks>
    public DateTime EndTime
    {
        get => _endTime;
        set { if (_endTime != value) { _endTime = value; OnPropertyChanged(); } }
    }

    private string _durationText = "";
    /// <summary>格式化好的时长文本，如 "1h23m"</summary>
    /// <remarks>自动刷新周期性重算进行中条目并回写，借此驱动 UI 更新</remarks>
    public string DurationText
    {
        get => _durationText;
        set { if (_durationText != value) { _durationText = value; OnPropertyChanged(); } }
    }

    /// <summary>属性变更事件：WPF 绑定引擎订阅它来更新对应单元格</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发 PropertyChanged 的辅助方法；省略参数时自动取调用方属性名</summary>
    /// <param name="propertyName">变更的属性名（编译期由 CallerMemberName 自动填入）</param>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName!));
}
