// —— 导入：System.Windows.Media 当前未在本文件直接引用，保留兼容 ——
using System.Windows.Media;

namespace TimeActivity.Models;

/// <summary>
/// 分类规则项 — 用于设置页规则列表显示
/// </summary>
/// <remarks>
/// Rule 的 UI 包装模型：CategoryId 外键换成 CategoryName 文本便于直接绑定；
/// 另提供 TypeLabel/TypeBrush 计算属性渲染"自定义/预置"标签。
/// 展示型 POCO（不实现变更通知），保存时由页面回查 CategoryId 落库。
/// </remarks>
public class RuleItem
{
    /// <summary>规则 Id（0 表示尚未入库的新规则）</summary>
    public int Id { get; set; }

    /// <summary>进程名</summary>
    /// <remarks>与 Rule.ProcessName 同义，建议保持小写</remarks>
    public string ProcessName { get; set; } = "";

    /// <summary>窗口标题关键词</summary>
    /// <remarks>空串表示不限标题，仅按进程名匹配</remarks>
    public string TitleKeyword { get; set; } = "";

    /// <summary>所属分类名（显示用，不是 Id）</summary>
    /// <remarks>下拉框直接绑定此文本；写库前反查出对应分类 Id</remarks>
    public string CategoryName { get; set; } = "";

    /// <summary>true=用户自定义可修改/删除，false=预置规则</summary>
    /// <remarks>默认 true：新建规则天然可编辑</remarks>
    public bool IsCustom { get; set; } = true;

    /// <summary>规则类型标签文字</summary>
    /// <remarks>计算属性：随 IsCustom 自动切换"自定义/预置"文案</remarks>
    public string TypeLabel => IsCustom ? "自定义" : "预置";

    /// <summary>规则类型标签颜色（自定义灰色，预置蓝色）</summary>
    /// <remarks>返回十六进制字符串而非 Brush 实例，由 XAML 转换器着色</remarks>
    public string TypeBrush => IsCustom ? "#999" : "#4A90D9";
}
