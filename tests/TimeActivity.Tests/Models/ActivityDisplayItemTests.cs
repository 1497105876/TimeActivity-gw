// ============================================================
// ActivityDisplayItemTests.cs — 显示模型的 INPC 通知契约测试
// ------------------------------------------------------------
// 为什么测这个：ActivityDisplayItem 是时间轴列表的绑定源，通知漏发
// 就是"数据改了界面不动"这类反人类体验。这不是杞人忧天 ——
// EndTime 就真实出过这个 bug（2026-09-02 修复，见源码注释），
// 下面那条回归测试就是为它立的碑。
//
// 契约要点：
//   Icon/DisplayName/WindowTitle/Category/DurationText/EndTime
//     —— setter 必须在"值真的变了"时发 PropertyChanged
//   相同值重复赋值 —— 不发（避免无谓重绘）
//   Id/ProcessName/StartTime —— 设计上就是一次性赋值，不发通知
//
// 说明：测 INPC 只是订阅 C# 事件，不创建 WPF 控件，不需要 STA 线程。
// Icon 用 null→null 验证即可，不构造 ImageSource 实例（那才需要图形栈）。
// ============================================================
using System.ComponentModel;
using TimeActivity.Models;
using Xunit;

namespace TimeActivity.Tests;

public class ActivityDisplayItemTests
{
    /// <summary>订阅通知并返回计数器，断言用</summary>
    private static (List<string> 属性名序列, Action 重置) 订阅通知(ActivityDisplayItem 条目)
    {
        var 收到的 = new List<string>();
        条目.PropertyChanged += (_, e) => 收到的.Add(e.PropertyName!);
        return (收到的, () => 收到的.Clear());
    }

    // ---------- 值变化必须发通知 ----------

    [Fact]
    public void EndTime_变化时发通知_回归2026_09_02修复()
    {
        var 条目 = new ActivityDisplayItem();
        var (序列, _) = 订阅通知(条目);

        条目.EndTime = DateTime.Now.AddMinutes(1);

        Assert.Equal(new[] { "EndTime" }, 序列);
    }

    [Theory]
    [InlineData("Category", "游戏")]
    [InlineData("DisplayName", "任务管理器")]
    [InlineData("WindowTitle", "文档.docx - Word")]
    [InlineData("DurationText", "1h23m")]
    public void 展示属性_变化时发通知且属性名正确(string 属性名, string 新值)
    {
        var 条目 = new ActivityDisplayItem();
        var (序列, _) = 订阅通知(条目);

        // 按属性名反射赋值，一份用例覆盖全部四个展示属性
        typeof(ActivityDisplayItem).GetProperty(属性名)!.SetValue(条目, 新值);

        Assert.Equal(new[] { 属性名 }, 序列);
    }

    // ---------- 相同值不发通知 ----------

    [Fact]
    public void Category_赋相同值不重复触发()
    {
        var 条目 = new ActivityDisplayItem { Category = "游戏" };
        var (序列, 重置) = 订阅通知(条目);

        条目.Category = "游戏";   // 与当前值相同，不应发通知

        Assert.Empty(序列);
    }

    [Fact]
    public void Icon_从null赋null不触发()
    {
        var 条目 = new ActivityDisplayItem();   // _icon 初始就是 null
        var (序列, _) = 订阅通知(条目);

        条目.Icon = null;

        Assert.Empty(序列);
    }

    // ---------- 一次性赋值属性：设计上不发通知 ----------

    [Fact]
    public void 标识属性_赋值不发通知()
    {
        var 条目 = new ActivityDisplayItem();
        var (序列, _) = 订阅通知(条目);

        条目.Id = 123;                 // 数据库主键
        条目.ProcessName = "chrome";   // 进程名
        条目.StartTime = DateTime.Now; // 开始时间

        // 这三个属性注释里写明"一次性赋值不触发"，通知了反而误导绑定端
        Assert.Empty(序列);
    }
}
