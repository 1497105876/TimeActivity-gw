// ============================================================================
// AppColorAllocator.cs — 应用专属颜色分配器（静态类）
// 职责：为每个进程名分配稳定且可区分的颜色：
//       自定义颜色(AppColors 表) > 已分配 > 按名称哈希从未用调色板取色；
//       LoadFromDb 预载缓存，SetCustom 写库并更新缓存。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace TimeActivity.Services;

/// <summary>
/// 应用颜色分配器 — 为新应用自动分配不重复的颜色
/// 预设调色板优先 → 用完随机+色差检查
/// </summary>
public static class AppColorAllocator
{
    // HSV 均匀分布的 24 色调色板（饱和度 0.75，亮度 0.65，明亮但不刺眼）
    private static readonly string[] Palette = GeneratePalette();

    // 内存缓存：进程名 → 颜色十六进制字符串
    private static readonly Dictionary<string, string> _cache = new();

    // 是否已从数据库加载过
    private static bool _loaded = false;

    // 缓存读写锁，避免多线程（UI 线程与后台分配）并发改 _cache 造成数据竞争
    private static readonly object _lock = new();

    /// <summary>
    /// 生成 24 色调色板：色相每 15° 取一个，饱和度 0.75，亮度 0.65。
    /// </summary>
    /// <returns>24 个十六进制颜色字符串</returns>
    private static string[] GeneratePalette()
    {
        var colors = new string[24];
        for (int i = 0; i < 24; i++)
        {
            double h = i * 15.0; // 0, 15, 30, ... 345
            var color = HsvToColor(h, 0.75, 0.65);
            colors[i] = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        return colors;
    }

    /// <summary>
    /// 从数据库加载所有应用颜色到内存缓存
    /// </summary>
    public static void LoadFromDb()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _cache.Clear();
            foreach (var (name, color) in Data.AppColorRepository.GetAll())
            {
                _cache[name] = color;
            }
            _loaded = true;
        }
    }

    /// <summary>
    /// 获取某个应用的颜色。如果有自定义就用自定义，否则自动分配一个不重复的。
    /// </summary>
    public static string GetOrAssign(string processName)
    {
        lock (_lock)
        {
            LoadFromDb();

            // 已有自定义颜色
            if (_cache.TryGetValue(processName, out var existing))
                return existing;

            // 自动分配
            var color = PickAvailableColor();
            _cache[processName] = color;
            Data.AppColorRepository.Set(processName, color);
            return color;
        }
    }

    /// <summary>
    /// 设置某个应用的自定义颜色
    /// </summary>
    public static void SetCustom(string processName, string color)
    {
        lock (_lock)
        {
            LoadFromDb();
            _cache[processName] = color;
            Data.AppColorRepository.Set(processName, color);
        }
    }

    /// <summary>
    /// 获取当前缓存中所有已占用的颜色
    /// </summary>
    public static HashSet<string> GetUsedColors()
    {
        lock (_lock)
        {
            LoadFromDb();
            return new HashSet<string>(_cache.Values);
        }
    }

    /// <summary>
    /// 挑一个还没被占用的颜色。调色板优先，用完了随机生成 + 色差检查避免颜色太接近。
    /// </summary>
    /// <returns>可用的颜色十六进制字符串</returns>
    private static string PickAvailableColor()
    {
        // 已在 GetOrAssign 的 lock 内，直接读 _cache 避免重复加锁
        var used = new HashSet<string>(_cache.Values);

        // 先从调色板里找一个没被占用的
        foreach (var c in Palette)
        {
            if (!used.Contains(c))
                return c;
        }

        // 调色板用完了，随机生成 HSV 颜色 + 色差检查
        var rng = new Random();
        string best = Palette[0];
        double bestMinDist = -1;

        for (int i = 0; i < 50; i++)
        {
            double h = rng.NextDouble() * 360;
            double s = 0.6 + rng.NextDouble() * 0.25; // 饱和度 0.6~0.85
            double v = 0.55 + rng.NextDouble() * 0.2;  // 亮度 0.55~0.75
            var candidate = HsvToColor(h, s, v);
            var candidateHex = $"#{candidate.R:X2}{candidate.G:X2}{candidate.B:X2}";

            // 找跟所有已用颜色最小色差最大的那个（最不容易撞色）
            double minDist = double.MaxValue;
            foreach (var usedColor in used)
            {
                var usedRgb = HexToColor(usedColor);
                double dist = ColorDistance(candidate, usedRgb);
                if (dist < minDist) minDist = dist;
            }

            if (minDist > bestMinDist)
            {
                bestMinDist = minDist;
                best = candidateHex;
            }

            // 色差够大就直接用，不用再试了
            if (bestMinDist >= 30.0) break;
        }

        return best;
    }

    // ========== HSV / 色差工具 ==========

    /// <summary>
    /// HSV 转 RGB 颜色。
    /// </summary>
    /// <param name="h">色相 0~360</param>
    /// <param name="s">饱和度 0~1</param>
    /// <param name="v">亮度 0~1</param>
    /// <returns>RGB 颜色</returns>
    private static Color HsvToColor(double h, double s, double v)
    {
        h = h % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>
    /// 十六进制颜色字符串转 RGB 颜色。
    /// </summary>
    /// <param name="hex">如 "#FF8800"</param>
    /// <returns>RGB 颜色</returns>
    private static Color HexToColor(string hex)
    {
        // 容错：空串或长度不对（如 #AARRGGBB 8 位）直接回退灰色，避免 FormatException 崩溃
        if (string.IsNullOrWhiteSpace(hex)) return Color.FromRgb(0x90, 0xA4, 0xAE);
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return Color.FromRgb(0x90, 0xA4, 0xAE);
        try
        {
            return Color.FromRgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch
        {
            return Color.FromRgb(0x90, 0xA4, 0xAE);
        }
    }

    /// <summary>
    /// 计算两个颜色的 RGB 欧氏距离，值越大颜色差异越大。够用了，不需要 Lab ΔE。
    /// </summary>
    private static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
