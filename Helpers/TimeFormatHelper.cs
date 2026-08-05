namespace TimeActivity.Helpers;

/// <summary>
/// 时长格式化工具 — 全项目统一用这一个
/// 输出格式: 65s / 2m5s / 1h2m5s / 1h30m
/// </summary>
public static class TimeFormatHelper
{
    public static string Format(int totalSeconds)
    {
        if (totalSeconds < 60) return $"{totalSeconds}s";

        int h = totalSeconds / 3600;
        int m = totalSeconds % 3600 / 60;
        int s = totalSeconds % 60;

        if (h > 0)
        {
            if (s == 0) return m == 0 ? $"{h}h" : $"{h}h{m}m";
            return $"{h}h{m}m{s}s";
        }
        // h == 0
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
