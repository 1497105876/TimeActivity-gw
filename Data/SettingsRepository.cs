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

    /// <summary>
    /// 默认设置值 — 单一数据源，DatabaseHelper.Initialize 和 BtnRestoreDefault 共用
    /// </summary>
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["PollIntervalSeconds"] = "3",
        ["IdleThresholdSeconds"] = "300",
        ["AutoStartTracking"] = "true",
        ["EnableScreenshot"] = "false",
        ["ScreenshotOnSwitch"] = "true",
        ["ScreenshotIntervalMinutes"] = "5",
        ["ScreenshotFormat"] = "jpg",
        ["ScreenshotPath"] = "",
        ["ScreenshotQuality"] = "medium",
        ["EnableMaxSize"] = "true",
        ["MaxScreenshotSizeMB"] = "5120",
        ["EnableMaxAge"] = "true",
        ["MaxScreenshotAgeDays"] = "30",
        ["ColorScheme"] = "default",
        ["Theme"] = "light",
        ["DataRetentionDays"] = "90",
        ["EnableAI"] = "true",
        ["AIMode"] = "lan",
        ["AIApiUrl"] = "http://localhost:11434",
        ["AIApiKey"] = "",
        ["AIModel"] = "qwen2.5:7b",
        ["AISummaryPath"] = "",
        ["AISummaryMaxCount"] = "30",
        ["AISummaryMaxSizeMB"] = "50",
        ["AutoStartWithWindows"] = "false",
        ["MinimizeToTray"] = "true",
    };

    /// <summary>
    /// 按页分组返回默认值（BtnRestoreDefault 用）
    /// </summary>
    public static Dictionary<string, string> GetDefaultsByPage(int navIndex) => navIndex switch
    {
        0 => FilterDefaults("PollIntervalSeconds", "IdleThresholdSeconds", "AutoStartTracking"),
        1 => FilterDefaults("EnableScreenshot", "ScreenshotOnSwitch", "ScreenshotIntervalMinutes",
            "ScreenshotFormat", "ScreenshotPath", "ScreenshotQuality",
            "EnableMaxSize", "MaxScreenshotSizeMB", "EnableMaxAge", "MaxScreenshotAgeDays"),
        4 => FilterDefaults("DataRetentionDays"),
        5 => FilterDefaults("EnableAI", "AIMode", "AIApiUrl", "AIApiKey", "AIModel"),
        6 => FilterDefaults("AutoStartWithWindows", "MinimizeToTray"),
        _ => new()
    };

    private static Dictionary<string, string> FilterDefaults(params string[] keys)
    {
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
            if (Defaults.TryGetValue(key, out var val))
                result[key] = val;
        return result;
    }
}
