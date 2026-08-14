namespace TimeActivity.Models;

/// <summary>
/// 分类规则 — 进程名/标题关键词 → 分类
/// </summary>
public class Rule
{
    /// <summary>数据库主键</summary>
    public int Id { get; set; }

    /// <summary>进程名，如 "chrome"（匹配规则的关键字段）</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题关键词（可选，进一步细化匹配）</summary>
    public string? TitleKeyword { get; set; }

    /// <summary>所属分类 Id</summary>
    public int CategoryId { get; set; }

    /// <summary>true=用户自定义规则，false=预置规则</summary>
    public bool IsCustom { get; set; }
}
