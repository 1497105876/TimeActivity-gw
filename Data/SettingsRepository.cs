using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 设置仓储 — 负责 Settings 表的读写
/// </summary>
public static class SettingsRepository
{
    // 确保数据库已初始化
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 按 Key 获取单个设置值
    /// </summary>
    /// <param name="key">设置项键名</param>
    /// <param name="defaultValue">未找到时的默认返回值</param>
    /// <returns>设置值字符串，未找到则返回 defaultValue</returns>
    public static string? Get(string key, string? defaultValue = null)
    {
        EnsureInit();
        const string sql = "SELECT Value FROM Settings WHERE Key = @Key";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Key", key);

        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? defaultValue : (string)result;
    }

    /// <summary>
    /// 设置某个配置项的值（存在则更新，不存在则插入）
    /// </summary>
    /// <param name="key">设置项键名</param>
    /// <param name="value">设置值</param>
    public static void Set(string key, string value)
    {
        EnsureInit();
        // UPSERT：Key 是 UNIQUE 的，冲突时更新 Value
        const string sql = @"
            INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = @Value";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Key", key);
        cmd.Parameters.AddWithValue("@Value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取全部设置项
    /// </summary>
    /// <returns>字典：键 → 值</returns>
    public static Dictionary<string, string> GetAll()
    {
        EnsureInit();
        var dict = new Dictionary<string, string>();
        using var conn = DbAccess.Open();
        // 查全部设置项，不做过滤
        using var cmd = new SqliteCommand("SELECT Key, Value FROM Settings", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        return dict;
    }

    /// <summary>
    /// 默认设置值——单一数据源，DatabaseHelper.Initialize 和 BtnRestoreDefault 共用
    /// </summary>
    public static readonly Dictionary<string, string> Defaults = new()
    {
        // 采集相关
        ["PollIntervalSeconds"] = "3",              // 轮询间隔（秒）
        ["IdleThresholdSeconds"] = "300",           // 空闲判定阈值（秒），5分钟无操作算空闲
        ["AutoStartTracking"] = "true",             // 启动后自动开始追踪
        // 截图相关
        ["EnableScreenshot"] = "false",             // 是否开启截图功能
        ["ScreenshotOnSwitch"] = "true",            // 切换窗口时截图
        ["ScreenshotIntervalMinutes"] = "5",        // 定时截图间隔（分钟）
        ["ScreenshotFormat"] = "jpg",               // 截图格式
        ["ScreenshotPath"] = "",                    // 截图保存路径（空=默认路径）
        ["ScreenshotQuality"] = "medium",           // 截图质量
        ["EnableMaxSize"] = "true",                 // 启用截图存储上限
        ["MaxScreenshotSizeMB"] = "5120",           // 截图最大占用空间（MB），5GB
        ["EnableMaxAge"] = "true",                  // 启用截图过期清理
        ["MaxScreenshotAgeDays"] = "30",            // 截图保留天数
        // 外观相关
        ["ColorScheme"] = "default",                // 颜色方案
        ["Theme"] = "light",                        // 主题
        // 数据相关
        ["DataRetentionDays"] = "90",               // 数据保留天数
        // AI 相关
        ["EnableAI"] = "true",                      // 是否启用 AI 总结
        ["AIMode"] = "lan",                         // AI 模式：lan=局域网，cloud=云端
        ["AIApiUrl"] = "http://localhost:11434",    // AI API 地址（默认本地 Ollama）
        ["AIApiKey"] = "",                          // AI API Key
        ["AIModel"] = "qwen2.5:7b",                 // AI 模型名称
        ["AISummaryPath"] = "",                    // AI 总结保存路径
        ["AISummaryMaxCount"] = "30",              // AI 总结最大保留条数
        ["AISummaryMaxSizeMB"] = "50",             // AI 总结最大占用空间（MB）
        // 系统相关
        ["AutoStartWithWindows"] = "false",        // 开机自启
        ["MinimizeToTray"] = "true",               // 关闭时最小化到托盘
    };

    /// <summary>
    /// 按设置页分组返回默认值（BtnRestoreDefault 用，每个页签只恢复对应设置项）
    /// </summary>
    /// <param name="navIndex">页签索引：0=常规，1=截图，4=数据，5=AI，6=系统</param>
    /// <returns>该页对应的默认设置字典</returns>
    public static Dictionary<string, string> GetDefaultsByPage(int navIndex) => navIndex switch
    {
        0 => FilterDefaults("PollIntervalSeconds", "IdleThresholdSeconds", "AutoStartTracking"),
        1 => FilterDefaults("EnableScreenshot", "ScreenshotOnSwitch", "ScreenshotIntervalMinutes",
            "ScreenshotFormat", "ScreenshotPath", "ScreenshotQuality",
            "EnableMaxSize", "MaxScreenshotSizeMB", "EnableMaxAge", "MaxScreenshotAgeDays"),
        4 => FilterDefaults("DataRetentionDays"),
        5 => FilterDefaults("EnableAI", "AIMode", "AIApiUrl", "AIApiKey", "AIModel",
            "AISummaryPath", "AISummaryMaxCount", "AISummaryMaxSizeMB"),
        6 => FilterDefaults("AutoStartWithWindows", "MinimizeToTray"),
        _ => new()
    };

    /// <summary>
    /// 从 Defaults 中筛选指定的 key 返回子字典
    /// </summary>
    /// <param name="keys">要筛选的键名数组</param>
    /// <returns>只包含指定键的字典</returns>
    private static Dictionary<string, string> FilterDefaults(params string[] keys)
    {
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
            if (Defaults.TryGetValue(key, out var val))
                result[key] = val;
        return result;
    }
}
