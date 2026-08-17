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

    /// <summary>
    /// 最近一个"已结束"周的周一。周报针对完整的一周（周一~周日），未结束的当前周不计入。
    /// 计算方式：取最近一个周日，再回退 6 天得到那周的周一。
    /// 例：周二→上周一；周一→上周一（上周日刚结束）；周日→上上周一（本周尚未结束）。
    /// </summary>
    public static DateTime GetLatestClosedWeekStart()
    {
        var today = DateTime.Today;
        int d = (int)today.DayOfWeek; // Sunday=0
        if (d == 0) d = 7;            // 周日当作 7，保证回退到上上周一
        var lastSunday = today.AddDays(-d);
        return lastSunday.AddDays(-6);
    }

    /// <summary>
    /// 最近一个"已结束"月的 1 号（月报针对完整自然月，未结束的当前月不计入）。
    /// </summary>
    public static DateTime GetLatestClosedMonthStart()
    {
        var today = DateTime.Today;
        return new DateTime(today.Year, today.Month, 1).AddMonths(-1);
    }
}
