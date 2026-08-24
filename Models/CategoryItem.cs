// —— 别名导入：SWMColor 明确指向 WPF 的 Color，避免与其它 Color 同名类型混淆 ——
using System.Windows.Media;
using SWMColor = System.Windows.Media.Color;
using TimeActivity.Helpers;

namespace TimeActivity.Models;

/// <summary>
/// 分类项 — 用于设置页分类管理显示
/// </summary>
/// <remarks>
/// Category 的 UI 包装模型：附加"能否删除/规则计数/现成颜色值"等绑定便利属性；
/// 展示型 POCO（不实现变更通知），修改后需手动保存才落库。
/// </remarks>
public class CategoryItem
{
    /// <summary>数据库主键</summary>
    /// <remarks>0 表示尚未入库的新分类；1~13 为预置分类不可删</remarks>
    public int Id { get; set; }

    /// <summary>十六进制颜色字符串</summary>
    /// <remarks>#RRGGBB / #AARRGGBB 格式；非法串由 ParseHex 回退默认色</remarks>
    public string Color { get; set; } = "#808080";

    /// <summary>分类名称</summary>
    public string Name { get; set; } = "";

    /// <summary>排序序号</summary>
    /// <remarks>越小越靠前</remarks>
    public int SortOrder { get; set; }

    /// <summary>预置分类（Id 1-13）不可删，自定义分类可以</summary>
    /// <remarks>计算属性：以 Id 魔数 13 作为预置/自定义分界</remarks>
    public bool CanDelete => Id > 13;

    /// <summary>该分类下有多少条规则（用于侧边栏显示）</summary>
    /// <remarks>加载规则列表后由页面回填，非数据库直读字段</remarks>
    public int Count { get; set; }

    /// <summary>把 Color 字符串解析成 WPF Color 对象，供绑定用</summary>
    /// <remarks>每次访问都重新解析；绑定频率低，开销可忽略</remarks>
    public SWMColor ColorValue => CategoryColorHelper.ParseHex(Color);
}
