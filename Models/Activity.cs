// —— 导入：DateTime 基础类型与 INotifyPropertyChanged 契约（本类型实际未实现通知） ——
using System;
using System.ComponentModel;

namespace TimeActivity.Models;

/// <summary>
/// 活动记录 — 每一条代表用户在某个软件上花了一段时间
/// </summary>
/// <remarks>
/// 纯数据载体（POCO）：不实现 INotifyPropertyChanged，属性变更不会主动通知 UI；
/// 时间均为本地时间，Duration 单位为秒，对应数据库 Activities 表的一行。
/// </remarks>
public class ActivityRecord
{
    /// <summary>数据库自增主键</summary>
    /// <remarks>仅作唯一标识/去重用，业务逻辑不应依赖其连续性</remarks>
    public long Id { get; set; }

    /// <summary>进程名，如 "chrome"、"devenv"</summary>
    /// <remarks>不含 ".exe" 后缀，作为规则匹配与统计分组的主维度</remarks>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题（当前活动窗口的标题文本）</summary>
    /// <remarks>配合 Rule.TitleKeyword 做更精细的分类匹配</remarks>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>分类名，由分类器根据规则匹配得出</summary>
    /// <remarks>无任何规则命中时的默认值为 "未分类"</remarks>
    public string Category { get; set; } = "未分类";

    /// <summary>活动开始时间</summary>
    /// <remarks>本地时间；TimeOfDay 用于时间轴上的秒偏移换算（0 点起算）</remarks>
    public DateTime StartTime { get; set; }

    /// <summary>活动结束时间（最后一条可能仍在进行中，等于当前时间）</summary>
    /// <remarks>可能跨午夜：StartTime 与 EndTime 分属两天，此时 EndTime 的当天秒偏移小于 StartTime 的偏移，
    /// 渲染端据此判断跨段并 +86400s 修正（见 TimelineRenderer）；不按自然日切分，整条记在起始日</remarks>
    public DateTime EndTime { get; set; }

    /// <summary>持续秒数 = EndTime - StartTime</summary>
    /// <remarks>单位：秒（int）；冗余存储便于聚合统计，免得每次都做时间差运算</remarks>
    public int Duration { get; set; } // 秒

    /// <summary>是否为空闲（用户无操作超过阈值时标记）</summary>
    /// <remarks>true 的记录在渲染器中被跳过/淡化，且不计入有效活跃时长</remarks>
    public bool IsIdle { get; set; }

    /// <summary>记录写入数据库的时间</summary>
    /// <remarks>新建对象时默认取当前时刻，入库前一般无需再赋值</remarks>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
