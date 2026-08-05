using System;

namespace TimeActivity.Helpers;

/// <summary>
/// 日期相关工具方法
/// </summary>
public static class DateHelper
{
    /// <summary>
    /// 获取指定日期所在周的周一（一周从周一开始）
    /// </summary>
    public static DateTime GetWeekStart(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-1 * diff).Date;
    }
}
