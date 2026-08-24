// 说明：本文件无额外 using，仅依赖命名空间内的基础类型
namespace TimeActivity.Models;

/// <summary>
/// 分类规则 — 进程名/标题关键词 → 分类
/// </summary>
/// <remarks>
/// 匹配语义：ProcessName 命中（通常忽略大小写），且 TitleKeyword 为空
/// 或被窗口标题包含时，该活动归入 CategoryId 指向的分类。
/// POCO 类型，对应数据库 rules 表。
/// </remarks>
public class Rule
{
    /// <summary>数据库主键</summary>
    public int Id { get; set; }

    /// <summary>进程名，如 "chrome"（匹配规则的关键字段）</summary>
    /// <remarks>建议小写存储；监控器采集的进程名会先规范化再匹配</remarks>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题关键词（可选，进一步细化匹配）</summary>
    /// <remarks>null 或空串表示不限标题：只要进程名命中即生效</remarks>
    public string? TitleKeyword { get; set; }

    /// <summary>所属分类 Id</summary>
    /// <remarks>外键指向 categories.Id；展示层通常联表换成分类名</remarks>
    public int CategoryId { get; set; }

    /// <summary>true=用户自定义规则，false=预置规则</summary>
    /// <remarks>false 时设置页禁止编辑/删除，升级时可被预置数据覆盖</remarks>
    public bool IsCustom { get; set; }
}
