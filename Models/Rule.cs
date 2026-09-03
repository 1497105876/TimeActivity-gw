// 说明：本文件无额外 using，仅依赖命名空间内的基础类型
namespace TimeActivity.Models;

/// <summary>
/// 分类规则 — 进程名精确匹配 或 标题关键词包含 → 分类
/// </summary>
/// <remarks>
/// 消费方为 ActivityClassifier（启动时整表读入内存），字段用途：
/// ProcessName 非空 → 进"进程名→分类"的精确匹配表，命中该进程即归类（不看标题）；
/// TitleKeyword 非空 → 进"标题包含→分类"的关键词表，供进程未命中时按标题兜底。
/// 两者可同时存在；标题关键词表只对没有进程规则的进程起作用。POCO 类型，对应数据库 Rules 表。
/// </remarks>
public class Rule
{
    /// <summary>数据库主键（Rules.Id）</summary>
    /// <remarks>0 表示尚未入库的新规则；入库后由数据库自增分配</remarks>
    public int Id { get; set; }

    /// <summary>进程名，如 "chrome"（匹配规则的关键字段，Rules.ProcessName）</summary>
    /// <remarks>来源为预置种子或用户从历史活动进程挑的进程名，均不含 ".exe"、不强制规范化大小写；
    /// 分类时按忽略大小写精确匹配（见 ActivityClassifier）。建议统一存小写便于维护</remarks>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题关键词（可选；Rules.TitleKeyword，可为 NULL）</summary>
    /// <remarks>null/空串 = 本条不走标题匹配（纯进程名规则）；非空 = 同时进"标题包含"关键词列表，
    /// 供浏览器进程和未命中进程做标题细分。注：预置规则固定存 NULL，用户规则留空会存空串，分类器把两者同等看待。</remarks>
    public string? TitleKeyword { get; set; }

    /// <summary>所属分类 Id（Rules.CategoryId）</summary>
    /// <remarks>外键指向 Categories.Id；展示层通常联表换成分类名</remarks>
    public int CategoryId { get; set; }

    /// <summary>true=用户自定义规则，false=预置规则（Rules.IsCustom）</summary>
    /// <remarks>默认 false 即预置；false 时设置页禁止编辑/删除，升级时可被预置数据覆盖</remarks>
    public bool IsCustom { get; set; }
}
