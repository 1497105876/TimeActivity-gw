// ============================================================
// AppDisplayNameTests.cs — 进程友好名解析器的哨兵分支测试
// ------------------------------------------------------------
// 只测两条硬编码哨兵分支，它们是跨环境稳定的：
//   null / 空串  → "未知"   （拿不到前台窗口时的兜底文案）
//   "(空闲)"     → "空闲"   （追踪引擎约定的空闲态哨兵进程名）
//
// 其他分支坚决不测，原因是断言不稳定：
//   Get("chrome") 的结果取决于"这台机器此刻有没有 chrome 进程在跑"
//   —— 本地开发八成开着 Chrome，返回 "Google Chrome"；
//      CI runner 上没有，返回进程名本身。
//   同一个测试两种结果，这不叫测试叫抽签。静态类 + 进程枚举 +
//   文件 IO 的组合只有做了依赖注入之后才能测出确定行为。
// ============================================================
using TimeActivity.Services;
using Xunit;

namespace TimeActivity.Tests;

public class AppDisplayNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Get_空进程名返回未知(string? 空进程名)
    {
        Assert.Equal("未知", AppDisplayName.Get(空进程名!));
    }

    [Fact]
    public void Get_空闲哨兵返回空闲()
    {
        Assert.Equal("空闲", AppDisplayName.Get("(空闲)"));
    }
}
