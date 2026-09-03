// ============================================================
// ActivityRecordTests.cs — 活动记录 POCO 的默认值契约
// ------------------------------------------------------------
// ActivityRecord 是纯数据载体，没什么逻辑可测，但两个默认值是
// 跨模块依赖的隐式契约，改坏了会出连锁问题：
//   Category 默认 "未分类" —— 分类器、仓储、渲染端都默认这个口径
//   CreatedAt 默认取当前时刻 —— 仓储层入库前通常不再赋值
// 这种测试的意义不在"测出 bug"，在于有人改默认值时 CI 会拦一下。
// ============================================================
using TimeActivity.Models;
using Xunit;

namespace TimeActivity.Tests;

public class ActivityRecordTests
{
    [Fact]
    public void 新记录_默认分类是未分类()
    {
        var 记录 = new ActivityRecord();

        Assert.Equal("未分类", 记录.Category);
    }

    [Fact]
    public void 新记录_创建时间默认取当前时刻()
    {
        var 记录 = new ActivityRecord();

        Assert.NotEqual(default, 记录.CreatedAt);
        // CreatedAt 在构造时就取值了，不可能早于测试开始太久；
        // 这里只断言它不是 default(DateTime)，不跟 DateTime.Now 硬比先后
    }
}
