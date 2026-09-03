// ============================================================================
// TimeFormatHelper.cs — 时长格式化工具（全项目唯一入口）
// 职责：把秒数转成 "45s / 2m5s / 1h23m / 1h30m" 风格可读文本，供时间轴/统计页/设置页共用。
// 口径约定：
//   - 不足 1 分钟：只展示秒（如 "0s"、"45s"）；
//   - 满分钟但秒为 0：只展示分（如 "30m"）；
//   - 满小时：必须展示"时+分+秒"，哪怕中间有 0 也保留（如 "1h0m5s"）；
//   - 不展示多余的单位，避免 "1h0m0s" 这类冗余形态。
// 用法：任意地方调用 TimeFormatHelper.Format(int) 即可，无需关心格式规则。
// ============================================================================
namespace TimeActivity.Helpers;

/// <summary>
/// 时长格式化工具 — 全项目统一用这一个
/// 输出格式: 45s / 2m5s / 1h2m5s / 1h30m
/// </summary>
public static class TimeFormatHelper
{
    /// <summary>
    /// 将秒数格式化为简洁的时长字符串
    /// </summary>
    /// <param name="totalSeconds">总秒数</param>
    /// <returns>格式化后的时长，如 45s / 2m5s / 1h2m5s / 1h30m</returns>
    public static string Format(int totalSeconds)
    {
        // 复用 long 版本的实现，避免两份逻辑
        return Format((long)totalSeconds);
    }

    /// <summary>
    /// 将秒数格式化为简洁的时长字符串（long 版本，支持超过 int.MaxValue 秒的超大时长）
    /// </summary>
    /// <param name="totalSeconds">总秒数</param>
    /// <returns>格式化后的时长，如 1m5s / 2m5s / 1h2m5s / 1h30m</returns>
    public static string Format(long totalSeconds)
    {
        // 调用方应保证传入非负秒数；负数一律先落进 <60 分支，原样带负号输出（如 -5s）
        // 不足 1 分钟（0~59s）直接显示秒，0 秒时即输出 "0s"
        if (totalSeconds < 60) return $"{totalSeconds}s";

        // 拆分成时分秒：整除 3600 得小时，余数再整除 60 得分钟，其余为秒
        long h = totalSeconds / 3600;
        long m = totalSeconds % 3600 / 60;
        long s = totalSeconds % 60;

        if (h > 0)
        {
            // 满 1 小时才进这层；小时数 h 一定非 0
            // 秒为 0 时：分钟也为 0 → 只输出 "{h}h"；分钟非 0 → 输出 "{h}h{m}m"
            if (s == 0) return m == 0 ? $"{h}h" : $"{h}h{m}m";
            // 秒非 0 则小时分钟秒全带上（分钟可能为 0，会出现 "1h0m5s" 这类形态）
            return $"{h}h{m}m{s}s";
        }
        // 没有小时（0~59 分钟之间）：秒为 0 只输出分钟，如 "30m"
        if (s == 0) return $"{m}m";
        // 秒非 0：分钟+秒组合输出，如 2m5s
        return $"{m}m{s}s";
    }
}
