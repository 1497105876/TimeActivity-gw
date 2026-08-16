using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 应用颜色仓储 — 管理每个应用的自定义颜色（独立于分类颜色）
/// </summary>
public static class AppColorRepository
{
    // 确保数据库已初始化
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
    /// 设置某个应用的颜色，存在则更新，不存在则插入（UPSERT）
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="color">十六进制颜色值，如 #FF6B6B</param>
    public static void Set(string processName, string color)
    {
        using var conn = DbAccess.Open();
        // ON CONFLICT 主键冲突时更新颜色，实现 UPSERT
        using var cmd = new SqliteCommand(@"
            INSERT INTO AppColors (ProcessName, Color) VALUES (@p, @c)
            ON CONFLICT(ProcessName) DO UPDATE SET Color=@c", conn);
        cmd.Parameters.AddWithValue("@p", processName);
        cmd.Parameters.AddWithValue("@c", color);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取某个应用的颜色
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>颜色字符串，不存在则返回 null</returns>
    public static string? Get(string processName)
    {
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand("SELECT Color FROM AppColors WHERE ProcessName=@p", conn);
        cmd.Parameters.AddWithValue("@p", processName);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return reader.GetString(0);
        return null;
    }
}
