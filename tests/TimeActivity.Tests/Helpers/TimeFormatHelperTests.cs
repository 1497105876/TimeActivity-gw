// ============================================================
// TimeFormatHelperTests.cs — 本项目的第一批单元测试
// ------------------------------------------------------------
// 为什么先测它：这是全项目最纯粹的一块逻辑 —— 给秒数、吐字符串，
// 不碰数据库、不碰界面、不碰系统时间，测试起来零负担。
// 目的不是覆盖率，是把整条 dotnet test 链路跑通，建立手感。
//
// 用例全部照着 TimeFormatHelper.cs 头部注释写的口径约定来：
//   - 不足 1 分钟只显示秒
//   - 满分钟但秒为 0 只显示分
//   - 满小时必须显示时+分，哪怕中间是 0（如 1h0m5s）
// 其中 3605 → "1h0m5s" 这条最值得留着，那是注释里特意强调的口径。
//
// 运行方式（三种都行，挑顺手的）：
//   dotnet test tests/TimeActivity.Tests/TimeActivity.Tests.csproj
//   cd tests/TimeActivity.Tests && dotnet test
//   dotnet test --filter "FullyQualifiedName~TimeFormatHelperTests"
// ============================================================
using TimeActivity.Helpers;
using Xunit;

namespace TimeActivity.Tests;

public class TimeFormatHelperTests
{
    [Theory]
    [InlineData(0, "0s")]          // 0 秒
    [InlineData(45, "45s")]        // 不足 1 分钟，只显示秒
    [InlineData(59, "59s")]        // 一分钟的边界前
    [InlineData(60, "1m")]         // 刚好 1 分钟，秒为 0 只显示分
    [InlineData(125, "2m5s")]
    [InlineData(1800, "30m")]
    [InlineData(3599, "59m59s")]   // 差一秒到一小时，还没有小时分量
    [InlineData(3600, "1h")]       // 刚好 1 小时，分秒都为 0
    [InlineData(3605, "1h0m5s")]   // 满小时必须保留 0 分，注释里的明确口径
    [InlineData(3660, "1h1m")]     // 秒为 0，显示时和分
    [InlineData(3725, "1h1m5s")]
    [InlineData(5425, "1h30m25s")]
    [InlineData(-5, "-5s")]        // 负数兜底，落进不足一分钟分支，原样带负号
    public void Format_按约定格式化(int 秒数, string 期望)
    {
        var 实际 = TimeFormatHelper.Format(秒数);

        Assert.Equal(期望, 实际);
    }

    [Theory]
    [InlineData(90061L, "25h1m1s")]           // 超过一天
    [InlineData(3000000000L, "833333h20m")]   // 超过 int.MaxValue，只有 long 版本接得住
    public void Format_long版本支持超大秒数(long 秒数, string 期望)
    {
        var 实际 = TimeFormatHelper.Format(秒数);

        Assert.Equal(期望, 实际);
    }

    [Fact]
    public void Format_int版本与long版本结果一致()
    {
        // int 版本内部只是转调 long 版本，抽几个值确认两条路不会跑偏
        foreach (var 秒数 in new[] { 0, 59, 60, 3725, int.MaxValue })
        {
            Assert.Equal(TimeFormatHelper.Format((long)秒数), TimeFormatHelper.Format(秒数));
        }
    }
}
