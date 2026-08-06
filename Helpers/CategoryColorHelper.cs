using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using TimeActivity.Data;

namespace TimeActivity.Helpers;

/// <summary>
/// 分类颜色管理 — 从数据库加载分类颜色，提供颜色查询
/// </summary>
public class CategoryColorHelper
{
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
        catch
        {
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
    /// 解析十六进制颜色字符串为 Color
    /// </summary>
    public static Color ParseHex(string hex)
        => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>
    /// 获取某个分类对应的颜色
    /// </summary>
    public Color GetColor(string category)
    {
        if (_colors.TryGetValue(category, out var hex))
            return ParseHex(hex);
        return ParseHex("#90A4AE");
    }

    /// <summary>
    /// 获取当前颜色字典
    /// </summary>
    public Dictionary<string, string> GetColors() => _colors;
}
