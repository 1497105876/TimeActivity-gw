// ============================================================
// CategoryColorHelperTests.cs — 分类颜色助手的单元测试
// ------------------------------------------------------------
// 只测不依赖数据库的部分：
//   ParseHex   —— 静态纯函数，十六进制/命名色 → WPF Color，非法值回退灰
//   GetHexBrush —— 静态，带缓存的冻结 Brush 工厂
//   GetColor   —— 实例方法，专门测"没调 Load 就直接用"的兜底路径
//            （此时 _colors 是空字典，任何分类名都应回退兜底灰 #90A4AE）
//
// 故意不测：Load()（直连 DatabaseHelper 的写死连接串，测试控制不了库内容）。
// 兜底色的 RGB：#90A4AE = R144 / G164 / B174。
// ============================================================
using System.Windows.Media;
using TimeActivity.Helpers;
using Xunit;

namespace TimeActivity.Tests;

public class CategoryColorHelperTests
{
    // ---------- ParseHex：颜色串解析 ----------

    [Fact]
    public void ParseHex_标准六位十六进制()
    {
        var 实际 = CategoryColorHelper.ParseHex("#4A90D9");

        Assert.Equal(Color.FromRgb(0x4A, 0x90, 0xD9), 实际);
    }

    [Fact]
    public void ParseHex_八位带透明度()
    {
        // #AARRGGBB 格式：AA 在最前面，别跟 ARGB 顺序搞混
        var 实际 = CategoryColorHelper.ParseHex("#80FFFFFF");

        Assert.Equal(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 实际);
    }

    [Fact]
    public void ParseHex_支持命名颜色()
    {
        var 实际 = CategoryColorHelper.ParseHex("Red");

        Assert.Equal(Colors.Red, 实际);
    }

    [Theory]
    [InlineData("not-a-color")]   // 纯乱串
    [InlineData("#12G45Z")]       // 含非法十六进制字符
    [InlineData("")]              // 空串
    [InlineData(null)]            // null
    public void ParseHex_非法值一律回退兜底灰(string? 非法值)
    {
        var 兜底灰 = Color.FromRgb(0x90, 0xA4, 0xAE);

        var 实际 = CategoryColorHelper.ParseHex(非法值!);

        Assert.Equal(兜底灰, 实际);
    }

    // ---------- GetHexBrush：冻结 Brush 缓存 ----------

    [Fact]
    public void GetHexBrush_返回的画刷已冻结()
    {
        // Freeze 是内存优化的关键：冻结后才能被 WPF 跨线程共享复用
        var brush = CategoryColorHelper.GetHexBrush("#4A90D9");

        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void GetHexBrush_同色串命中缓存返回同一实例()
    {
        var 第一次 = CategoryColorHelper.GetHexBrush("#ABCDEF");
        var 第二次 = CategoryColorHelper.GetHexBrush("#ABCDEF");

        Assert.Same(第一次, 第二次);
    }

    [Fact]
    public void GetHexBrush_忽略大小写命中同一缓存()
    {
        // 缓存字典是 OrdinalIgnoreCase，大小写不同也算同一个 key
        var 小写 = CategoryColorHelper.GetHexBrush("#abcdef");
        var 大写 = CategoryColorHelper.GetHexBrush("#ABCDEF");

        Assert.Same(小写, 大写);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetHexBrush_空白串直接用兜底灰(string? 空白值)
    {
        var 期望 = Color.FromRgb(0x90, 0xA4, 0xAE);

        var brush = CategoryColorHelper.GetHexBrush(空白值!);

        Assert.Equal(期望, brush.Color);
    }

    // ---------- GetColor：未加载缓存时的兜底 ----------

    [Fact]
    public void GetColor_没调Load就查_任何分类都回退灰()
    {
        // 新实例 _colors 是空字典，这条路径覆盖的是"启动早期渲染先于加载"的兜底
        var helper = new CategoryColorHelper();

        var 实际 = helper.GetColor("不存在的分类");

        Assert.Equal(Color.FromRgb(0x90, 0xA4, 0xAE), 实际);
    }
}
