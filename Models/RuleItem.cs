// —— 导入：System.Windows.Media 当前未在本文件直接引用，保留兼容 ——
using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 分类规则项 — 用于设置页规则列表显示
/// </summary>
/// <remarks>
/// Rule 的 UI 包装模型：CategoryId 外键换成 CategoryName 文本便于直接绑定/分组；
/// 另提供 TypeLabel/TypeBrush 计算属性，需要展示"自定义/预置"徽标时取文案与色值。
/// 展示型 POCO（不实现变更通知），保存时由 RuleRepository.SaveAll 回查 CategoryId 落库。
/// 加载路径见 SettingsWindow.Rules.LoadRules：真实规则行与"未分类"占位行都显式逐字段赋值。
/// </remarks>
public class RuleItem
{
    /// <summary>规则 Id（Rules.Id；0 表示尚未入库的新规则）</summary>
    public int Id { get; set; }

    /// <summary>进程名（Rules.ProcessName）</summary>
    /// <remarks>与 Rule.ProcessName 同义；分类匹配忽略大小写，故大小写不影响匹配</remarks>
    public string ProcessName { get; set; } = "";

    /// <summary>窗口标题关键词（Rules.TitleKeyword）</summary>
    /// <remarks>空串表示本规则不做标题匹配，仅按进程名归类；规则加载时 DB 的 NULL 会被映射成 ""（见 SettingsWindow.Rules）</remarks>
    public string TitleKeyword { get; set; } = "";

    /// <summary>所属分类名（显示用，不是 Id）</summary>
    /// <remarks>设置页按此字段分组展示；保存时由 SaveAll 反查出分类 Id 写进 Rules.CategoryId</remarks>
    public string CategoryName { get; set; } = "";

    /// <summary>true=用户自定义可修改/删除，false=预置规则（Rules.IsCustom）</summary>
    /// <remarks>代码中加载/保存时总是显式赋值（占位行也设为 false），不依赖此默认值</remarks>
    public bool IsCustom { get; set; } = true;

    /// <summary>规则类型标签文字</summary>
    /// <remarks>计算属性：随 IsCustom 自动切换"自定义/预置"文案（当前 UI 未见引用，属预留展示）</remarks>
    public string TypeLabel => IsCustom ? "自定义" : "预置";

    /// <summary>规则类型标签颜色（自定义灰色，预置蓝色）</summary>
    /// <remarks>返回十六进制字符串；是否需要着色由展示端决定（当前 UI 未见引用，属预留展示）</remarks>
    public string TypeBrush => IsCustom ? "#999" : "#4A90D9";
}
