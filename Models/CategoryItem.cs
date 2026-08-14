using System.Windows.Media;
using SWMColor = System.Windows.Media.Color;
using TimeActivity.Helpers;

namespace TimeActivity.Models;

/// <summary>
/// 分类项 — 用于设置页分类管理显示
/// </summary>
public class CategoryItem
{
    public int Id { get; set; }
    public string Color { get; set; } = "#808080";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool CanDelete => Id > 13; // 预置分类（Id 1-13）不可删
    public int Count { get; set; } // 该分类下的规则数

    public SWMColor ColorValue => CategoryColorHelper.ParseHex(Color);
}
