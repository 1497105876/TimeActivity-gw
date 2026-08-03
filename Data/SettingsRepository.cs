using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 设置仓储 — 负责 Settings 表的读写
/// </summary>
public static class SettingsRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    public static string? Get(string key, string? defaultValue = null)
    {
        EnsureInit();
        const string sql = "SELECT Value FROM Settings WHERE Key = @Key";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Key", key);

        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? defaultValue : (string)result;
    }

    public static void Set(string key, string value)
    {
        EnsureInit();
        const string sql = @"
            INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = @Value";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Key", key);
        cmd.Parameters.AddWithValue("@Value", value);
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<string, string> GetAll()
    {
        EnsureInit();
        var dict = new Dictionary<string, string>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT Key, Value FROM Settings", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        return dict;
    }
}
