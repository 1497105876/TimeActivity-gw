using System;
using System.ComponentModel;

namespace TimeActivity.Models;

/// <summary>
/// 活动记录 — 每一条代表用户在某个软件上花了一段时间
/// </summary>
public class ActivityRecord
{
    public long Id { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string Category { get; set; } = "未分类";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Duration { get; set; } // 秒
    public bool IsIdle { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
