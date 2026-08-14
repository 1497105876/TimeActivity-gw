using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 分类规则项 — 用于设置页规则列表显示
/// </summary>
public class RuleItem
{
    /// <summary>规则 Id（0 表示尚未入库的新规则）</summary>
    public int Id { get; set; }

    /// <summary>进程名</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>窗口标题关键词</summary>
    public string TitleKeyword { get; set; } = "";

    /// <summary>所属分类名（显示用，不是 Id）</summary>
    public string CategoryName { get; set; } = "";

    /// <summary>true=用户自定义可修改/删除，false=预置规则</summary>
    public bool IsCustom { get; set; } = true;

    /// <summary>规则类型标签文字</summary>
    public string TypeLabel => IsCustom ? "自定义" : "预置";

    /// <summary>规则类型标签颜色（自定义灰色，预置蓝色）</summary>
    public string TypeBrush => IsCustom ? "#999" : "#4A90D9";
}
