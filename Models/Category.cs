// —— 导入：当前无实际使用的类型，保留以备扩展 ——
using System;

namespace TimeActivity.Models;

/// <summary>
/// 分类 — 时间块归属的维度（如预置的"开发工具""社交通讯"，见 CategoryRepository.PresetCategories）
/// </summary>
/// <remarks>
/// 对应数据库 Categories 表；预置分类占用固定 Id 1~13，自定义分类从更大 Id 起。
/// POCO 类型，不做属性变更通知。
/// </remarks>
public class Category
{
    /// <summary>数据库主键（Categories.Id）</summary>
    /// <remarks>1~13 为不可删除的预置分类（分界常量见 CategoryRepository.MaxPresetCategoryId）；0 表示尚未入库的新分类</remarks>
    public int Id { get; set; }

    /// <summary>分类名称，如 "开发工具"、"社交通讯"（Categories.Name）</summary>
    /// <remarks>ActivityRecord.Category 冗余保存的就是该字符串；改名后历史记录不会自动跟随</remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>十六进制颜色，如 "#4A90D9"（Categories.Color）</summary>
    /// <remarks>#RRGGBB 或 #AARRGGBB 格式；由 CategoryColorHelper.ParseHex 解析；默认 #808080 中性灰</remarks>
    public string Color { get; set; } = "#808080";

    /// <summary>图标标识（预留字段，当前未使用）（Categories.Icon）</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>排序序号，越小越靠前（Categories.SortOrder）</summary>
    /// <remarks>设置页分类列表按 SortOrder,Id 排序（见 CategoryRepository.GetAll）；颜色缓存加载也按它 ORDER BY（见 CategoryColorHelper.Load）。统计页行按聚合时长降序，与此字段无关</remarks>
    public int SortOrder { get; set; }
}
