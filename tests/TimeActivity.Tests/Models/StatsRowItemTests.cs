// ============================================================
// StatsRowItemTests.cs — 统计行模型的计算属性测试
// ------------------------------------------------------------
// StatsRowItem 有 6 个纯计算属性（不碰任何外部依赖）：
//   DurationText  = TimeFormatHelper.Format(TotalSeconds)
//   PercentText   = $"{Percent:F1}%"     ← 固定 1 位小数
//   PercentOnBar  = Percent > 80         ← 严格大于，80 本身不算
//   BarFillWidth  = Percent / 100
//   BarEmptyWidth = 1 - BarFillWidth
//   TextOnEmpty   = !PercentOnBar        ← 与 PercentOnBar 互补
//
// 浮点断言全部用 xUnit 的精度重载（比较到小数点后 10 位）：
// 比如 1 - 0.8 在 double 里是 0.19999999999999996 而不是 0.2，
// 直接 Assert.Equal(0.2, x) 会因为第 16 位小数挂掉，这不是 bug 是 IEEE 754。
//
// 文化说明：F1 的小数点符号取当前 culture，zh-CN 和 CI 的 en-US 都是 "."，
// 断言安全。哪天真要支持德语区再加 InvariantCulture 处理。
// ============================================================
using TimeActivity.Models;
using Xunit;

namespace TimeActivity.Tests;

public class StatsRowItemTests
{
    // ---------- DurationText：复用 TimeFormatHelper ----------

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(3660, "1h1m")]
    [InlineData(5425, "1h30m25s")]
    public void DurationText_按统一时长格式输出(int 总秒数, string 期望)
    {
        var 行 = new StatsRowItem { TotalSeconds = 总秒数 };

        Assert.Equal(期望, 行.DurationText);
    }

    // ---------- PercentText：固定 1 位小数 ----------

    [Theory]
    [InlineData(50.0, "50.0%")]
    [InlineData(0.0, "0.0%")]
    [InlineData(100.0, "100.0%")]
    [InlineData(33.333, "33.3%")]     // F1 舍去
    [InlineData(66.666, "66.7%")]     // F1 进位
    public void PercentText_固定保留一位小数(double 百分比, string 期望)
    {
        var 行 = new StatsRowItem { Percent = 百分比 };

        Assert.Equal(期望, 行.PercentText);
    }

    // ---------- PercentOnBar：80% 阈值边界 ----------

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(79.9, false)]
    [InlineData(80.0, false)]         // 边界：注释写的是">80"，80 本身不算
    [InlineData(80.1, true)]
    [InlineData(100.0, true)]
    public void PercentOnBar_严格大于80才叠印(double 百分比, bool 期望)
    {
        var 行 = new StatsRowItem { Percent = 百分比 };

        Assert.Equal(期望, 行.PercentOnBar);
    }

    // ---------- 比例条宽度：互补凑满整行 ----------

    [Theory]
    [InlineData(0.0, 0.0, 1.0)]
    [InlineData(50.0, 0.5, 0.5)]
    [InlineData(100.0, 1.0, 0.0)]
    public void 有色与空余宽度互补且凑满整行(double 百分比, double 期望填充, double 期望空余)
    {
        var 行 = new StatsRowItem { Percent = 百分比 };

        // 精度 10 位：只关心业务意义上的比例，不管 IEEE 754 第 16 位的抖动
        Assert.Equal(期望填充, 行.BarFillWidth, 10);
        Assert.Equal(期望空余, 行.BarEmptyWidth, 10);
        Assert.Equal(1.0, 行.BarFillWidth + 行.BarEmptyWidth, 10);   // 恒等式：两段永远拼满
    }

    [Fact]
    public void 文字摆放位置与叠印标记互补()
    {
        var 低占比 = new StatsRowItem { Percent = 30.0 };
        var 高占比 = new StatsRowItem { Percent = 90.0 };

        // 低占比时文字在空余区，高占比时叠在色条上，两者必须互补
        Assert.True(低占比.TextOnEmpty);
        Assert.False(低占比.PercentOnBar);
        Assert.False(高占比.TextOnEmpty);
        Assert.True(高占比.PercentOnBar);
    }
}
