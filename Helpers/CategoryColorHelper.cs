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
/// <remarks>
/// 用法：先调用 Load() 建好缓存，之后 GetColor/GetBrush 按分类名取色；
/// 解析与取 Brush 的静态方法 ParseHex/GetHexBrush 不依赖 Load，可直接用。
/// 所有"查不到"的路径统一回退中性灰蓝 #90A4AE，渲染端不用判空。
/// </remarks>
public class CategoryColorHelper
{
    /// <summary>分类名 → 颜色字符串（#RRGGBB/#AARRGGBB）的缓存字典</summary>
    /// <remarks>实例字段，仅 Load() 写入、GetColor/GetBrush 读取；从不用 Add 累积，整表重建避免脏数据</remarks>
    private Dictionary<string, string> _colors = new();

    /// <summary>
    /// 从数据库加载全部分类颜色（按 SortOrder 排序）
    /// </summary>
    /// <returns>加载好的 分类名→颜色 字典（失败时退化为预置分类的配色）</returns>
    public Dictionary<string, string> Load()
    {
        // 每次都重建缓存字典，避免残留已被删除分类的旧颜色
        _colors = new Dictionary<string, string>();
        try
        {
            // 打开到 SQLite 的连接（连接串集中定义在 DatabaseHelper）
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
            // 失败后字典是空的，直接用预置分类重建（元组第 1 项名称、第 2 项颜色）
            _colors = new Dictionary<string, string>();
            foreach (var (name, color, _, _) in CategoryRepository.PresetCategories)
                _colors[name] = color;
        }
        return _colors;
    }

    /// <summary>
    /// 解析十六进制颜色字符串为 Color（非法值回退灰色）
    /// </summary>
    /// <param name="hex">颜色字符串，如 "#4A90D9"、"#80FFFFFF"；也可传命名色如 "Red"</param>
    /// <returns>解析出的 WPF Color；非法格式或 null 时回退 #90A4AE</returns>
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

    // ==================== 冻结 Brush 缓存（2026-08-25 内存优化） ====================
    // 背景：渲染器每次重绘都 new SolidColorBrush，未 Freeze 的 Brush 不参与跨线程共享、
    // 每个都是独立对象，60s 自动刷新 + 交互重绘持续产生大量短期对象。
    // 已 Freeze 的 Brush 可被 WPF 内部缓存复用；分类色数量有限，全量缓存收益显著。
    /// <summary>保护 _hexBrushCache 的锁对象；GetHexBrush 的读写都在这个锁里串行化</summary>
    private static readonly object _brushLock = new();
    /// <summary>颜色字符串 → 已冻结 SolidColorBrush 的静态缓存</summary>
    /// <remarks>key 用 OrdinalIgnoreCase 忽略大小写；字典值都是 Freeze 过的 Brush，可跨线程共享</remarks>
    private static readonly Dictionary<string, SolidColorBrush> _hexBrushCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按十六进制颜色串取已冻结 Brush（带缓存；非法值回退灰色）。</summary>
    /// <param name="hex">颜色字符串；null/空白会先归一成 #90A4AE</param>
    /// <returns>已 Freeze 的 SolidColorBrush，可安全用于渲染</returns>
    public static SolidColorBrush GetHexBrush(string hex)
    {
        // 空白串当成没给颜色，直接走兜底色，省得进解析再兜一次
        string key = string.IsNullOrWhiteSpace(hex) ? "#90A4AE" : hex;
        lock (_brushLock)
        {
            // 缓存命中直接返回，避免每次画图都 new 一个 Brush
            if (_hexBrushCache.TryGetValue(key, out var cached)) return cached;
            // 未命中：解析成 Color → 建 Brush → Freeze（冻结后才能被 WPF 跨线程复用）→ 入缓存
            var brush = new SolidColorBrush(ParseHex(key));
            brush.Freeze();
            _hexBrushCache[key] = brush;
            return brush;
        }
    }

    /// <summary>按分类名取已冻结 Brush（未收录分类回退灰色）。</summary>
    /// <param name="category">分类名；查不到按 #90A4AE 兜底</param>
    /// <returns>该分类对应的已冻结 Brush</returns>
    public SolidColorBrush GetBrush(string category)
        // 查到用分类自己的颜色串，查不到（未收录/新分类）回退兜底色
        => GetHexBrush(_colors.TryGetValue(category, out var hex) ? hex : "#90A4AE");

    /// <summary>
    /// 获取某个分类对应的颜色，找不到则回退灰色
    /// </summary>
    /// <param name="category">分类名，用于在 _colors 缓存里查找</param>
    /// <returns>该分类的 WPF Color 对象；未收录分类统一返回兜底灰蓝 #90A4AE</returns>
    public Color GetColor(string category)
    {
        // 命中：按分类存储的颜色串解析成 Color
        if (_colors.TryGetValue(category, out var hex))
            return ParseHex(hex);
        // 未命中（还没 Load / 分类被删但旧记录残留）也回退同一灰色，保证渲染不中断
        return ParseHex("#90A4AE");
    }

}
