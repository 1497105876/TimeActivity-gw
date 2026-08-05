using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TimeActivity.Models;

namespace TimeActivity.Data;

/// <summary>
/// 规则仓储 — 负责 Rules 表的增删查
/// </summary>
public static class RuleRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    public static List<Rule> GetAll()
    {
        EnsureInit();
        var list = new List<Rule>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT Id, ProcessName, TitleKeyword, CategoryId, IsCustom FROM Rules ORDER BY Id", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Rule
            {
                Id = reader.GetInt32(0),
                ProcessName = reader.GetString(1),
                TitleKeyword = reader.IsDBNull(2) ? null : reader.GetString(2),
                CategoryId = reader.GetInt32(3),
                IsCustom = reader.GetBoolean(4)
            });
        }
        return list;
    }

    public static void Insert(string processName, string titleKeyword, int categoryId)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@P, @T, @C, 1)", conn);
        cmd.Parameters.AddWithValue("@P", processName);
        cmd.Parameters.AddWithValue("@T", string.IsNullOrEmpty(titleKeyword) ? (object)System.DBNull.Value : titleKeyword);
        cmd.Parameters.AddWithValue("@C", categoryId);
        cmd.ExecuteNonQuery();
    }

    public static void ClearAll()
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("DELETE FROM Rules WHERE IsCustom=1", conn);
        cmd.ExecuteNonQuery();
    }
}
