using System.Windows.Media;

namespace TimeActivity;

/// <summary>
/// 分类规则项 — 用于设置页规则列表显示
/// </summary>
public class RuleItem
{
    public int Id { get; set; }
    public string ProcessName { get; set; } = "";
    public string TitleKeyword { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public bool IsCustom { get; set; } = true; // true=可删，false=预置不可删
    public string TypeLabel => IsCustom ? "自定义" : "预置";
    public string TypeBrush => IsCustom ? "#999" : "#4A90D9";
}
