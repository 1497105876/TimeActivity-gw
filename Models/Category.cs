using System;

namespace TimeActivity.Models;

/// <summary>
/// 分类 — 比如开发、娱乐、社交、学习
/// </summary>
public class Category
{
    /// <summary>数据库主键</summary>
    public int Id { get; set; }

    /// <summary>分类名称，如 "开发"、"娱乐"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>十六进制颜色，如 "#4A90D9"</summary>
    public string Color { get; set; } = "#808080";

    /// <summary>图标标识（预留字段）</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>排序序号，越小越靠前</summary>
    public int SortOrder { get; set; }
}
