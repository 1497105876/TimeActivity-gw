using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 应用颜色仓储 — 管理每个应用的自定义颜色（独立于分类颜色）
/// </summary>
public static class AppColorRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 获取所有应用颜色，返回 Dictionary&lt;进程名, 颜色&gt;
    /// </summary>
    public static Dictionary<string, string> GetAll()
    {
        EnsureInit();
        var dict = new Dictionary<string, string>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT ProcessName, Color FROM AppColors", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetString(1);
        }
        return dict;
    }

    /// <summary>
    /// 设置某个应用的颜色（UPSERT）
    /// </summary>
    public static void Set(string processName, string color)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(@"
            INSERT INTO AppColors (ProcessName, Color) VALUES (@p, @c)
            ON CONFLICT(ProcessName) DO UPDATE SET Color=@c", conn);
        cmd.Parameters.AddWithValue("@p", processName);
        cmd.Parameters.AddWithValue("@c", color);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取某个应用的颜色，没有返回 null
    /// </summary>
    public static string? Get(string processName)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT Color FROM AppColors WHERE ProcessName=@p", conn);
        cmd.Parameters.AddWithValue("@p", processName);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return reader.GetString(0);
        return null;
    }
}
