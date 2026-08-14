using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity.Helpers;

/// <summary>
/// 分类颜色管理 — 从数据库加载分类颜色，提供颜色查询
/// </summary>
public class CategoryColorHelper
{
    // 分类名 → 颜色字符串的缓存字典
    private Dictionary<string, string> _colors = new();

    /// <summary>
    /// 从数据库加载全部分类颜色
    /// </summary>
    public Dictionary<string, string> Load()
    {
        _colors = new Dictionary<string, string>();
        try
        {
            using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT Name, Color FROM Categories ORDER BY SortOrder", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                _colors[reader.GetString(0)] = reader.GetString(1);
        }
        catch (Exception ex)
        {
            // 数据库读取失败时用硬编码预置颜色兜底，保证 UI 不崩
            Logger.Error("加载分类颜色失败，使用默认值", ex);
            _colors = new Dictionary<string, string>
            {
                { "开发工具", "#4A90D9" }, { "社交通讯", "#E67E22" }, { "游戏", "#E74C3C" },
                { "办公学习", "#2ECC71" }, { "浏览器", "#9B59B6" }, { "视频娱乐", "#FF6B6B" },
                { "音乐", "#AB47BC" }, { "设计创作", "#FFA726" }, { "实用工具", "#26C6DA" },
                { "AI助手", "#EC407A" }, { "系统组件", "#7CB9E8" },
                { "空闲", "#CFD8DC" }, { "未分类", "#90A4AE" },
            };
        }
        return _colors;
    }

    /// <summary>
    /// 解析十六进制颜色字符串为 Color（非法值回退灰色）
    /// </summary>
    public static Color ParseHex(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString("#90A4AE");
        }
    }

    /// <summary>
    /// 获取某个分类对应的颜色，找不到则回退灰色
    /// </summary>
    /// <param name="category">分类名</param>
    /// <returns>该分类的 WPF Color 对象</returns>
    public Color GetColor(string category)
    {
        // 先从缓存字典查，查到就解析；查不到回退灰色
        if (_colors.TryGetValue(category, out var hex))
            return ParseHex(hex);
        return ParseHex("#90A4AE");
    }

    /// <summary>
    /// 获取当前加载的颜色字典（分类名 → 颜色字符串）
    /// </summary>
    /// <returns>颜色字典的引用</returns>
    public Dictionary<string, string> GetColors() => _colors;
}
