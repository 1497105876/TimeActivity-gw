using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TimeActivity.Models;

namespace TimeActivity.Data;

/// <summary>
/// 分类仓储 — 负责 Categories 表的增删改查
/// </summary>
public static class CategoryRepository
{
    /// <summary>
    /// 预置分类的最大 Id，超过此值的都是用户自定义分类
    /// </summary>
    public const int MaxPresetCategoryId = 13;

    // 确保数据库已初始化
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 获取全部分类，按 SortOrder 和 Id 排序
    /// </summary>
    /// <returns>分类列表，按排序字段升序</returns>
    public static List<Category> GetAll()
    {
        EnsureInit();
        var list = new List<Category>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        // 按 SortOrder 排序，SortOrder 相同的按 Id 排
        using var cmd = new SqliteCommand("SELECT Id, Name, Color, Icon, SortOrder FROM Categories ORDER BY SortOrder, Id", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Category
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Color = reader.GetString(2),
                Icon = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SortOrder = reader.GetInt32(4)
            });
        }
        return list;
    }

    /// <summary>
    /// 更新或插入分类。Id > 0 时更新已有分类，否则插入新分类
    /// </summary>
    /// <param name="id">分类 Id，大于 0 表示更新，否则插入</param>
    /// <param name="name">分类名称</param>
    /// <param name="color">十六进制颜色值</param>
    /// <param name="sortOrder">排序序号</param>
    public static int UpdateOrInsert(int id, string name, string color, int sortOrder)
    {
        using var conn = DbAccess.Open();
        if (id > 0)
        {
            // 更新已有分类
            using var cmd = new SqliteCommand(
                "UPDATE Categories SET Name=@Name, Color=@Color, SortOrder=@Sort WHERE Id=@Id", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Color", color);
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
            return id;
        }
        else
        {
            // 插入新分类，Icon 默认空字符串
            using var cmd = new SqliteCommand(
                "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, '', @Sort); SELECT last_insert_rowid();", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Color", color);
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            return (int)(long)cmd.ExecuteScalar();
        }
    }

    /// <summary>
    /// 只更新分类颜色（右键快捷改色用）
    /// </summary>
    public static void UpdateColor(string name, string color)
    {
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(
            "UPDATE Categories SET Color=@Color WHERE Name=@Name", conn);
        cmd.Parameters.AddWithValue("@Color", color);
        cmd.Parameters.AddWithValue("@Name", name);
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
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand("DELETE FROM Categories WHERE Id=@Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 重置到预置分类：删除自定义分类（Id > 13），重置预置分类颜色和排序
    /// </summary>
    public static void ResetToDefault()
    {
        using var conn = DbAccess.Open();

        // 删除自定义分类
        using (var delCmd = new SqliteCommand("DELETE FROM Categories WHERE Id > " + MaxPresetCategoryId, conn))
            delCmd.ExecuteNonQuery();

        // 重置预置分类的颜色和排序
        // 预置分类的名称、颜色、排序——用于重置时恢复默认值
        var defaults = new[]
        {
            ("开发工具", "#4A90D9", 1),
            ("社交通讯", "#E67E22", 2),
            ("游戏", "#E74C3C", 3),
            ("办公学习", "#2ECC71", 4),
            ("浏览器", "#9B59B6", 5),
            ("视频娱乐", "#FF6B6B", 6),
            ("音乐", "#AB47BC", 7),
            ("设计创作", "#FFA726", 8),
            ("实用工具", "#26C6DA", 9),
            ("AI助手", "#EC407A", 10),
            ("系统组件", "#7CB9E8", 11),
            ("空闲", "#CFD8DC", 12),
            ("未分类", "#90A4AE", 13),
        };

        foreach (var (name, color, order) in defaults)
        {
            using var updCmd = new SqliteCommand(
                "UPDATE Categories SET Color=@Color, SortOrder=@SortOrder WHERE Name=@Name", conn);
            updCmd.Parameters.AddWithValue("@Color", color);
            updCmd.Parameters.AddWithValue("@SortOrder", order);
            updCmd.Parameters.AddWithValue("@Name", name);
            updCmd.ExecuteNonQuery();
        }
    }
}
