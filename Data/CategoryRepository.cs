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
    /// 删除自定义分类（预置 Id 1-8 不可删）
    /// </summary>
    public static bool Delete(int id)
    {
        if (id <= 8) return false;
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("DELETE FROM Categories WHERE Id=@Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
