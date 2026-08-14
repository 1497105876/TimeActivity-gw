using System;
using System.ComponentModel;

namespace TimeActivity.Models;

/// <summary>
/// 活动记录 — 每一条代表用户在某个软件上花了一段时间
/// </summary>
public class ActivityRecord
{
    /// <summary>数据库自增主键</summary>
    public long Id { get; set; }

    /// <summary>进程名，如 "chrome"、"devenv"</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题（当前活动窗口的标题文本）</summary>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>分类名，由分类器根据规则匹配得出</summary>
    public string Category { get; set; } = "未分类";

    /// <summary>活动开始时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>活动结束时间（最后一条可能仍在进行中，等于当前时间）</summary>
    public DateTime EndTime { get; set; }

    /// <summary>持续秒数 = EndTime - StartTime</summary>
    public int Duration { get; set; } // 秒

    /// <summary>是否为空闲（用户无操作超过阈值时标记）</summary>
    public bool IsIdle { get; set; }

    /// <summary>记录写入数据库的时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
