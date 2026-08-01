using System;

namespace TimeActivity.Models;

/// <summary>
/// 分类 — 比如开发、娱乐、社交、学习
/// </summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#808080";
    public string Icon { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
