// ============================================================
// DateHelperTests.cs — DateHelper 的单元测试
// ------------------------------------------------------------
// 这一轮只测两个纯函数：ToDateKey 和 GetWeekStart。
// 特点跟 TimeFormatHelper 一样：输入全在参数里，不读系统时间、
// 不碰数据库，测试想喂什么日期就喂什么日期。
//
// GetLatestClosedWeekStart / GetLatestClosedMonthStart 这轮故意没测：
// 它们内部自己去读 DateTime.Today，测试没法控制"今天"是哪天，
// 断言写不出确定的期望值。要测得先给方法加可选参数
// （GetLatestClosedWeekStart(DateTime? 今天 = null)），
// 那属于改现有代码，等确认了再动。
//
// 期望值全部用脚本按 2026 年真实日历复核过（跨月/跨年都验了），
// 上次 TimeFormatHelper 那个 3725 的算术翻车不能再来一次。
// ============================================================
using TimeActivity.Helpers;
using Xunit;

namespace TimeActivity.Tests;

public class DateHelperTests
{
    // ---------- ToDateKey：日期转数据库统一 key（yyyy-MM-dd） ----------

    [Theory]
    [InlineData(2026, 8, 24, "2026-08-24")]
    [InlineData(2026, 1, 5, "2026-01-05")]      // 单位的月和日必须补前导零
    [InlineData(2026, 12, 31, "2026-12-31")]
    public void ToDateKey_输出统一格式(int 年, int 月, int 日, string 期望)
    {
        var 实际 = new DateTime(年, 月, 日).ToDateKey();

        Assert.Equal(期望, 实际);
    }

    [Fact]
    public void ToDateKey_时间分量不影响结果()
    {
        // 库里按天聚合就靠这个 key，下午三点存的记录必须跟凌晨零点归到同一天
        var 带时间 = new DateTime(2026, 8, 24, 15, 30, 45);

        Assert.Equal("2026-08-24", 带时间.ToDateKey());
    }

    // ---------- GetWeekStart：返回所在周的周一（一周从周一开始） ----------
    // 参照日历：2026-08-31 是周一，2026-09-06 是周日，2026-09-07 是下周一。

    [Theory]
    [InlineData("2026-08-31", "2026-08-31")]   // 周一：自身就是起点
    [InlineData("2026-09-01", "2026-08-31")]   // 周二：往前跨月回到八月
    [InlineData("2026-09-03", "2026-08-31")]   // 周四
    [InlineData("2026-09-06", "2026-08-31")]   // 周日：一周最后一天，仍归本周
    [InlineData("2026-09-07", "2026-09-07")]   // 下周一：新的一周从今天算
    [InlineData("2027-01-01", "2026-12-28")]   // 跨年：元旦所在周的周一是上一年
    public void GetWeekStart_返回所在周的周一(string 输入, string 期望)
    {
        var 实际 = DateHelper.GetWeekStart(DateTime.Parse(输入));

        Assert.Equal(DateTime.Parse(期望), 实际);
    }

    [Fact]
    public void GetWeekStart_时间分量被清成零点()
    {
        // 注释里写明返回"周一 0 点整"，深夜 23:59 和凌晨 0:00 必须落回同一天
        var 深夜 = new DateTime(2026, 8, 31, 23, 59, 59);

        var 结果 = DateHelper.GetWeekStart(深夜);

        Assert.Equal(new DateTime(2026, 8, 31), 结果);
    }
}
