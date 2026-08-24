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
        // 每次都重建缓存字典，避免残留已被删除分类的旧颜色
        _colors = new Dictionary<string, string>();
        try
        {
            using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
            conn.Open();
            // 按 SortOrder 排序读取 Categories 表的 名称→颜色 两列
            using var cmd = new SqliteCommand(
                "SELECT Name, Color FROM Categories ORDER BY SortOrder", conn);
            using var reader = cmd.ExecuteReader();
            // 逐行填充缓存：key=分类名 value=颜色字符串（#RRGGBB）
            while (reader.Read())
                _colors[reader.GetString(0)] = reader.GetString(1);
        }
        catch (Exception ex)
        {
            // 数据库读取失败时用预置分类颜色兜底（复用权威定义），保证 UI 不崩且颜色不再重复维护
            Logger.Error("加载分类颜色失败，使用默认值", ex);
            _colors = new Dictionary<string, string>();
            foreach (var (name, color, _, _) in CategoryRepository.PresetCategories)
                _colors[name] = color;
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
            // ColorConverter 支持 #RRGGBB / #AARRGGBB 及命名颜色字符串
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            // 非法格式或 null → 回退中性灰蓝色 #90A4AE
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
        // 缓存未命中（未收录/新分类）也回退同一灰色，保证渲染不中断
        return ParseHex("#90A4AE");
    }

}
