using System.Windows.Media;
using SWMColor = System.Windows.Media.Color;
using TimeActivity.Helpers;

namespace TimeActivity.Models;

/// <summary>
/// 分类项 — 用于设置页分类管理显示
/// </summary>
public class CategoryItem
{
    /// <summary>数据库主键</summary>
    public int Id { get; set; }

    /// <summary>十六进制颜色字符串</summary>
    public string Color { get; set; } = "#808080";

    /// <summary>分类名称</summary>
    public string Name { get; set; } = "";

    /// <summary>排序序号</summary>
    public int SortOrder { get; set; }

    /// <summary>预置分类（Id 1-13）不可删，自定义分类可以</summary>
    public bool CanDelete => Id > 13;

    /// <summary>该分类下有多少条规则（用于侧边栏显示）</summary>
    public int Count { get; set; }

    /// <summary>把 Color 字符串解析成 WPF Color 对象，供绑定用</summary>
    public SWMColor ColorValue => CategoryColorHelper.ParseHex(Color);
}
