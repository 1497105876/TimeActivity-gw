// ============================================================================
// CategoryRepository.cs — Categories 表的仓储（静态类）
// 职责：分类 CRUD、颜色更新、预置分类(PresetCategories/MaxPresetCategoryId)
//       权威定义、恢复默认(ResetToDefault)、UpdateOrInsert 供设置页整体保存。
// 预置分类 Id ≤ MaxPresetCategoryId 不可删除（UI 与保存逻辑共同遵守）。
// ============================================================================
// 泛型集合（List）
using System.Collections.Generic;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;
// 数据模型（Category）
using TimeActivity.Models;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 分类仓储 — 负责 Categories 表的增删改查。
/// 预置分类的权威定义在本类内（PresetCategories），
/// Activities.Category 存的是分类“名称”字符串快照，删除/改名不会自动回填历史记录。
/// </summary>
public static class CategoryRepository
{
    /// <summary>
    /// 预置分类的最大 Id，超过此值的都是用户自定义分类
    /// 取值必须与下面 PresetCategories 的条目数保持一致（当前正好 13 条，Id 分配为 1..13）：
    /// 播种时是按数组顺序 INSERT 的，AUTOINCREMENT 从 1 开始依次分配，两者天然对齐。
    /// 这个常量同时被 Delete（拒绝删预置）、ResetToDefault（只删 Id > 13 的）和设置页 UI 用来判断能否删除。
    /// </summary>
    public const int MaxPresetCategoryId = 13;

    /// <summary>
    /// 预置分类的权威定义（名称、颜色、图标、排序序号）。
    /// 全程序只此一处维护，所有预置分类的来源都引用这里，
    /// 避免分类颜色/图标在多处硬编码导致不同步。
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Color, string Icon, int SortOrder)> PresetCategories = new[]
    {
        // Id=1 开发工具：蓝色系
        ("开发工具", "#4A90D9", "code", 1),
        // Id=2 社交通讯：橙色系
        ("社交通讯", "#E67E22", "chat", 2),
        // Id=3 游戏：红色系
        ("游戏", "#E74C3C", "gamepad", 3),
        // Id=4 办公学习：绿色系
        ("办公学习", "#2ECC71", "book", 4),
        // Id=5 浏览器：紫色系
        ("浏览器", "#9B59B6", "globe", 5),
        // Id=6 视频娱乐：珊瑚红
        ("视频娱乐", "#FF6B6B", "video", 6),
        // Id=7 音乐：紫红系
        ("音乐", "#AB47BC", "music", 7),
        // Id=8 设计创作：橙黄系
        ("设计创作", "#FFA726", "palette", 8),
        // Id=9 实用工具：青色系
        ("实用工具", "#26C6DA", "wrench", 9),
        // Id=10 AI助手：玫红系
        ("AI助手", "#EC407A", "robot", 10),
        // Id=11 系统组件：浅蓝系
        ("系统组件", "#7CB9E8", "desktop", 11),
        // Id=12 空闲：灰蓝占位色
        ("空闲", "#CFD8DC", "coffee", 12),
        // Id=13 未分类：灰色兜底色
        ("未分类", "#90A4AE", "question", 13),
    };

    // 确保数据库已初始化（首次调用触发建表与预置分类播种）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 获取全部分类，按 SortOrder 和 Id 排序
    /// </summary>
    /// <returns>分类列表，按排序字段升序</returns>
    public static List<Category> GetAll()
    {
        // 初始化检查：保证 Categories 表已存在
        EnsureInit();
        // 结果容器
        var list = new List<Category>();
        // 创建连接（指向统一权威连接字符串）
        // 注意：本类是少数几个没走 DbAccess.Open() 的地方（GetAll 与 RuleRepository.GetAll 同款写法），
        //       因为已经显式调了 EnsureInit()，功能等价，只是样板代码多一点
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 打开连接
        conn.Open();
        // 按 SortOrder 排序，SortOrder 相同的按 Id 排
        // 全表读取：分类条目固定在十几条，不需要任何过滤或分页
        using var cmd = new SqliteCommand("SELECT Id, Name, Color, Icon, SortOrder FROM Categories ORDER BY SortOrder, Id", conn);
        // 执行查询得到游标
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // 按列序映射到实体
            list.Add(new Category
            {
                // 自增主键
                Id = reader.GetInt32(0),
                // 分类名称
                Name = reader.GetString(1),
                // 十六进制颜色
                Color = reader.GetString(2),
                // 图标名，NULL 防御为空串
                Icon = reader.IsDBNull(3) ? "" : reader.GetString(3),
                // 排序序号
                SortOrder = reader.GetInt32(4)
            });
        }
        // 返回全部分类
        return list;
    }

    /// <summary>
    /// 更新或插入分类。Id > 0 时更新已有分类，否则插入新分类
    /// </summary>
    /// <param name="id">分类 Id，大于 0 表示更新，否则插入</param>
    /// <param name="name">分类名称</param>
    /// <param name="color">十六进制颜色值</param>
    /// <param name="sortOrder">排序序号</param>
    /// <returns>更新时返回原 Id；插入时返回新生成的自增 Id</returns>
    public static int UpdateOrInsert(int id, string name, string color, int sortOrder)
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        if (id > 0)
        {
            // 更新已有分类
            // 注意：Icon 不在更新范围内（设置页不编辑图标）
            using var cmd = new SqliteCommand(
                "UPDATE Categories SET Name=@Name, Color=@Color, SortOrder=@Sort WHERE Id=@Id", conn);
            // 新名称
            cmd.Parameters.AddWithValue("@Name", name);
            // 新颜色
            cmd.Parameters.AddWithValue("@Color", color);
            // 新排序号
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            // 主键定位
            cmd.Parameters.AddWithValue("@Id", id);
            // 执行更新；行不存在时静默无效果（受影响 0 行）
            cmd.ExecuteNonQuery();
            // 更新路径原样返回传入 Id
            return id;
        }
        else
        {
            // 插入新分类，Icon 默认空字符串
            // 批语句：INSERT 后取 last_insert_rowid() 返回新主键
            using var cmd = new SqliteCommand(
                "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, '', @Sort); SELECT last_insert_rowid();", conn);
            // 名称参数
            cmd.Parameters.AddWithValue("@Name", name);
            // 颜色参数
            cmd.Parameters.AddWithValue("@Color", color);
            // 排序号参数
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            // 标量结果为新自增 Id（long → int 收窄）
            return (int)(long)cmd.ExecuteScalar();
        }
    }

    /// <summary>
    /// 只更新分类颜色（右键快捷改色用）
    /// </summary>
    /// <param name="name">分类名称</param>
    /// <param name="color">十六进制颜色值</param>
    public static void UpdateColor(string name, string color)
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 按名称定位只改 Color 一列（名称是业务上的自然键）
        using var cmd = new SqliteCommand(
            "UPDATE Categories SET Color=@Color WHERE Name=@Name", conn);
        // 绑定新颜色
        cmd.Parameters.AddWithValue("@Color", color);
        // 绑定分类名
        cmd.Parameters.AddWithValue("@Name", name);
        // 执行更新；同名不存在时无效果
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除自定义分类。预置分类（Id 1-13）不可删除
    /// </summary>
    /// <param name="id">要删除的分类 Id</param>
    /// <returns>删除成功返回 true，预置分类或不存在则返回 false</returns>
    public static bool Delete(int id)
    {
        // 预置分类 Id 1-13 受保护，不允许删除
        if (id <= MaxPresetCategoryId) return false;
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 按主键删除
        using var cmd = new SqliteCommand("DELETE FROM Categories WHERE Id=@Id", conn);
        // 绑定主键参数
        cmd.Parameters.AddWithValue("@Id", id);
        // 受影响行数 > 0 视为删除成功
        // 注意：Rules.CategoryId 与 Activities.Category 的历史引用不会被级联清理
        // ——Rules 会留下指向已删分类 Id 的悬挂外键（连接字符串没开 PRAGMA foreign_keys，删的时候不会报错），
        //   Activities 存的是分类名快照，删了分类后历史记录里还会显示那个名字；
        //   这两处都由上层（设置页保存 / ReclassifyAll）负责兜底
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 重置到预置分类：删除自定义分类（Id > 13），重置预置分类颜色和排序
    /// </summary>
    public static void ResetToDefault()
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();

        // 删除自定义分类
        // Id 为常量整数拼接，无注入风险
        using (var delCmd = new SqliteCommand("DELETE FROM Categories WHERE Id > " + MaxPresetCategoryId, conn))
            delCmd.ExecuteNonQuery();

        // 重置预置分类的颜色、图标和排序——直接复用权威定义 PresetCategories，
        // 既避免重复硬编码，也把图标一并还原（旧实现只还原颜色与排序，会丢掉图标）。
        foreach (var (name, color, icon, order) in PresetCategories)
        {
            // 按名称逐个还原三列属性
            using var updCmd = new SqliteCommand(
                "UPDATE Categories SET Color=@Color, Icon=@Icon, SortOrder=@SortOrder WHERE Name=@Name", conn);
            // 还原颜色
            updCmd.Parameters.AddWithValue("@Color", color);
            // 还原图标
            updCmd.Parameters.AddWithValue("@Icon", icon);
            // 还原排序号
            updCmd.Parameters.AddWithValue("@SortOrder", order);
            // 名称定位条件
            updCmd.Parameters.AddWithValue("@Name", name);
            // 执行还原（若该预置分类被改名过则匹配不到，静默跳过）
            updCmd.ExecuteNonQuery();
        }
    }
}
