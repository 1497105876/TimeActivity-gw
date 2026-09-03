// —— 导入：INPC 契约、CallerMemberName 特性、图标用的 ImageSource ——
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 活动列表绑定的显示模型。实现 INotifyPropertyChanged，
/// 这样后续原地更新某条已有条目（时长/分类/结束时间）时，ListView 能立刻反映出来。
/// </summary>
/// <remarks>
/// 具备通知能力的展示属性（setter 内都会触发 PropertyChanged）：Icon/DisplayName/
/// WindowTitle/Category/DurationText/EndTime；Id/ProcessName/StartTime 为一次性赋值的
/// 标识与原始时间，不触发通知。运行时实际更新的属性有限：60s 自动刷新会改
/// _items[0] 的 EndTime/DurationText，设置保存或右键改分类会回写 Category。
/// </remarks>
public class ActivityDisplayItem : INotifyPropertyChanged
{
    /// <summary>数据库主键，用于去重</summary>
    /// <remarks>自动刷新用 Id 集合判断哪些是新记录，避免与 OnActivityRecorded 重复插入同一条</remarks>
    public long Id { get; set; }

    /// <summary>Icon 的后备字段；null 表示尚未提取到图标</summary>
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

    /// <summary>DisplayName 的后备字段；默认空串</summary>
    private string _displayName = "";
    /// <summary>友好显示名（如 "任务管理器" 而非 "taskmgr"）</summary>
    /// <remarks>由 CreateDisplayItem 同步解析回填（解析不到就退化为进程名），不走异步补全</remarks>
    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    /// <summary>WindowTitle 的后备字段；默认空串表示"无标题"</summary>
    private string _windowTitle = "";
    /// <summary>窗口标题</summary>
    /// <remarks>记录结束时从 ActivityRecord 一次性拷入（CreateDisplayItem），不会动态跟随前台变化</remarks>
    public string WindowTitle
    {
        get => _windowTitle;
        set { if (_windowTitle != value) { _windowTitle = value; OnPropertyChanged(); } }
    }

    /// <summary>Category 的后备字段；默认空串，尚未赋值前的临时值</summary>
    private string _category = "";
    /// <summary>分类名</summary>
    /// <remarks>创建时即从 ActivityRecord 带入；规则变更/改分类后由界面重算回写并通知刷新</remarks>
    public string Category
    {
        get => _category;
        set { if (_category != value) { _category = value; OnPropertyChanged(); } }
    }

    /// <summary>开始时间</summary>
    /// <remarks>一次性赋值，不触发通知</remarks>
    public DateTime StartTime { get; set; }

    /// <summary>EndTime 的后备字段；进行中的活动在刷新时被周期性更新</summary>
    private DateTime _endTime;
    /// <summary>结束时间</summary>
    /// <remarks>2026-09-02 修复：补 INPC 通知 —— XAML"结束"列绑定此属性，此前 60s tick 更新
    /// 进行中活动的 EndTime 时界面不刷新（检查报告 2.4/3.5）。StartTime 一次性赋值仍不通知。</remarks>
    public DateTime EndTime
    {
        get => _endTime;
        set { if (_endTime != value) { _endTime = value; OnPropertyChanged(); } }
    }

    /// <summary>DurationText 的后备字段；空串表示尚未计算时长</summary>
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
