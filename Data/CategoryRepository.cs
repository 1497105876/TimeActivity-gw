using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TimeActivity.Models;

namespace TimeActivity.Data;

/// <summary>
/// 分类仓储 — 负责 Categories 表的增删改查
/// </summary>
public static class CategoryRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    public static List<Category> GetAll()
    {
        EnsureInit();
        var list = new List<Category>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
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

    public static void UpdateOrInsert(int id, string name, string color, int sortOrder)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        if (id > 0)
        {
            using var cmd = new SqliteCommand(
                "UPDATE Categories SET Name=@Name, Color=@Color, SortOrder=@Sort WHERE Id=@Id", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Color", color);
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = new SqliteCommand(
                "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, '', @Sort)", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Color", color);
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 删除自定义分类（预置 Id 1-13 不可删）
    /// </summary>
    public static bool Delete(int id)
    {
        if (id <= 13) return false;
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("DELETE FROM Categories WHERE Id=@Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 重置到预置分类：删除自定义分类（Id > 13），重置预置分类颜色和排序
    /// </summary>
    public static void ResetToDefault()
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();

        // 删除自定义分类
        using (var delCmd = new SqliteCommand("DELETE FROM Categories WHERE Id > 13", conn))
            delCmd.ExecuteNonQuery();

        // 重置预置分类的颜色和排序
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
