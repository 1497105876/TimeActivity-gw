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
    /// <param name="date">目标日期；格式串里只有日期部分，时间分量不影响输出</param>
    /// <returns>形如 "2026-08-24" 的日期字符串，供按天聚合/查库用</returns>
    public static string ToDateKey(this DateTime date)
    {
        // 按统一格式输出，如 "2026-08-24"
        return date.ToString(DateKeyFormat);
    }
    /// <summary>
    /// 获取指定日期所在周的周一（一周从周一开始）
    /// </summary>
    /// <param name="date">落在哪个自然周（周一~周日）就返回那一周的起点</param>
    /// <returns>该周周一的 0 点整（时间部分已通过 .Date 清掉）</returns>
    public static DateTime GetWeekStart(DateTime date)
    {
        // DayOfWeek 枚举 Sunday=0；(7+(星期几-周一))%7 = 距本周一的天数(0~6)
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        // 回退 diff 天并取 .Date 去掉时间部分，得到周一 0 点
        return date.AddDays(-1 * diff).Date;
    }

    /// <summary>
    /// 最近一个"已结束"周的周一。周报针对完整的一周（周一~周日），未结束的当前周不计入。
    /// 思路：往回找到最近一个"已经过完的周日"，那周必然完整，返回它的周一。
    /// 周一到周六：上个周日是前几天，直接可得；周日时"今天"还没过完不能算，
    /// 需按 7 天再往前取更早那个周日（即跳到再前一周的周一）。
    /// </summary>
    /// <returns>最近一个完整周的周一 0 点；再往前加 6 天即该周周日</returns>
    public static DateTime GetLatestClosedWeekStart()
    {
        // 以今天为基准向回推算
        var today = DateTime.Today;
        int d = (int)today.DayOfWeek; // Sunday=0
        if (d == 0) d = 7;            // 周日当作 7：保证落回"上一个已过完的周日"，而非把当天当结束
        // 回退 d 天得到最近一个已过去的周日（该周日所在周必已完整结束）
        var lastSunday = today.AddDays(-d);
        // 再回退 6 天即得那周的周一（周一~周日完整一周的起点）
        return lastSunday.AddDays(-6);
    }

    /// <summary>
    /// 最近一个"已结束"月的 1 号（月报针对完整自然月，未结束的当前月不计入）。
    /// </summary>
    /// <returns>上一个完整自然月的 1 号 0 点；当月任何一天调用都返回同一个月</returns>
    public static DateTime GetLatestClosedMonthStart()
    {
        // 以今天为基准：本月 1 号再回退一个月即上月 1 号
        var today = DateTime.Today;
        return new DateTime(today.Year, today.Month, 1).AddMonths(-1);
    }
}
