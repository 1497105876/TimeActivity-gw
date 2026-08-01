namespace TimeActivity.Models;

/// <summary>
/// 分类规则 — 进程名/标题关键词 → 分类
/// </summary>
public class Rule
{
    public int Id { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? TitleKeyword { get; set; }
    public int CategoryId { get; set; }
    public bool IsCustom { get; set; }
}
