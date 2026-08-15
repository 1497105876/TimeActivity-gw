using System;

namespace TimeActivity.Helpers;

/// <summary>
/// 日期相关工具方法
/// </summary>
public static class DateHelper
{
    /// <summary>
    /// 数据库里存日期用的统一格式字符串（yyyy-MM-dd）
    /// </summary>
    public const string DateKeyFormat = "yyyy-MM-dd";

    /// <summary>
    /// 把日期转成数据库统一的字符串 key（yyyy-MM-dd）
    /// </summary>
    public static string ToDateKey(this DateTime date)
    {
        return date.ToString(DateKeyFormat);
    }
    /// <summary>
    /// 获取指定日期所在周的周一（一周从周一开始）
    /// </summary>
    public static DateTime GetWeekStart(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-1 * diff).Date;
    }
}
