namespace TimeActivity.Helpers;

/// <summary>
/// 时长格式化工具 — 全项目统一用这一个
/// 输出格式: 65s / 2m5s / 1h2m5s / 1h30m
/// </summary>
public static class TimeFormatHelper
{
    /// <summary>
    /// 将秒数格式化为简洁的时长字符串
    /// </summary>
    /// <param name="totalSeconds">总秒数</param>
    /// <returns>格式化后的时长，如 65s / 2m5s / 1h2m5s / 1h30m</returns>
    public static string Format(int totalSeconds)
    {
        // 不足 1 分钟直接显示秒
        if (totalSeconds < 60) return $"{totalSeconds}s";

        // 拆分成时分秒
        int h = totalSeconds / 3600;
        int m = totalSeconds % 3600 / 60;
        int s = totalSeconds % 60;

        if (h > 0)
        {
            // 有小时：秒为 0 省略秒，分钟为 0 省略分钟
            if (s == 0) return m == 0 ? $"{h}h" : $"{h}h{m}m";
            return $"{h}h{m}m{s}s";
        }
        // 没有小时：秒为 0 省略秒
        if (s == 0) return $"{m}m";
        return $"{m}m{s}s";
    }

    /// <summary>
    /// long 重载（AI 总结用的 long 类型）
    /// </summary>
    public static string Format(long totalSeconds)
    {
        return Format((int)totalSeconds);
    }
}
