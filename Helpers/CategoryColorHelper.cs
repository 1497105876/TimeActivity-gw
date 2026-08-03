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
                { "开发", "#4A90D9" }, { "社交", "#E67E22" }, { "娱乐", "#E74C3C" },
                { "学习", "#2ECC71" }, { "系统", "#95A5A6" }, { "网页", "#9B59B6" },
                { "空闲", "#BDC3C7" }, { "未分类", "#7F8C8D" },
            };
        }
        return _colors;
    }

    /// <summary>
    /// 获取某个分类对应的颜色
    /// </summary>
    public Color GetColor(string category)
    {
        if (_colors.TryGetValue(category, out var hex))
            return (Color)ColorConverter.ConvertFromString(hex);
        return (Color)ColorConverter.ConvertFromString("#7F8C8D");
    }

    /// <summary>
    /// 获取当前颜色字典
    /// </summary>
    public Dictionary<string, string> GetColors() => _colors;
}
