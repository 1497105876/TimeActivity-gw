// —— 导入：当前无实际使用的类型，保留以备扩展 ——
using System;

namespace TimeActivity.Models;

/// <summary>
/// 分类 — 比如开发、娱乐、社交、学习
/// </summary>
/// <remarks>
/// 对应数据库 categories 表；预置分类占用固定 Id 1~13，自定义分类从更大 Id 起。
/// POCO 类型，不做属性变更通知。
/// </remarks>
public class Category
{
    /// <summary>数据库主键</summary>
    /// <remarks>1~13 为不可删除的预置分类（参见 CategoryItem.CanDelete）</remarks>
    public int Id { get; set; }

    /// <summary>分类名称，如 "开发"、"娱乐"</summary>
    /// <remarks>ActivityRecord.Category 冗余保存的就是该字符串</remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>十六进制颜色，如 "#4A90D9"</summary>
    /// <remarks>#RRGGBB 或 #AARRGGBB 格式；由 CategoryColorHelper.ParseHex 解析</remarks>
    public string Color { get; set; } = "#808080";

    /// <summary>图标标识（预留字段）</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>排序序号，越小越靠前</summary>
    /// <remarks>设置页/统计页按此字段排序展示</remarks>
    public int SortOrder { get; set; }
}
