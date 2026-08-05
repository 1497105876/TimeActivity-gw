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

    // 内存缓存：进程名 → 颜色
    private static readonly Dictionary<string, string> _cache = new();

    // 已加载标志
    private static bool _loaded = false;

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
        if (_loaded) return;
        _cache.Clear();
        foreach (var (name, color) in Data.AppColorRepository.GetAll())
        {
            _cache[name] = color;
        }
        _loaded = true;
    }

    /// <summary>
    /// 获取某个应用的颜色。如果有自定义就用自定义，否则自动分配一个不重复的。
    /// </summary>
    public static string GetOrAssign(string processName)
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

    /// <summary>
    /// 设置某个应用的自定义颜色
    /// </summary>
    public static void SetCustom(string processName, string color)
    {
        LoadFromDb();
        _cache[processName] = color;
        Data.AppColorRepository.Set(processName, color);
    }

    /// <summary>
    /// 获取当前缓存中所有已占用的颜色
    /// </summary>
    public static HashSet<string> GetUsedColors()
    {
        LoadFromDb();
        return new HashSet<string>(_cache.Values);
    }

    /// <summary>
    /// 挑一个还没被占用的颜色。调色板优先，用完了随机+色差检查。
    /// </summary>
    private static string PickAvailableColor()
    {
        var used = GetUsedColors();

        // 先从调色板找
        foreach (var c in Palette)
        {
            if (!used.Contains(c))
                return c;
        }

        // 调色板用完了，随机生成 + 色差检查
        var rng = new Random();
        string best = Palette[0];
        double bestMinDist = -1;

        for (int i = 0; i < 50; i++)
        {
            double h = rng.NextDouble() * 360;
            double s = 0.6 + rng.NextDouble() * 0.25; // 0.6~0.85
            double v = 0.55 + rng.NextDouble() * 0.2; // 0.55~0.75
            var candidate = HsvToColor(h, s, v);
            var candidateHex = $"#{candidate.R:X2}{candidate.G:X2}{candidate.B:X2}";

            // 找跟所有已用颜色最小色差最大的那个（最不容易撞）
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

            // 色差够大就直接用
            if (bestMinDist >= 30.0) break;
        }

        return best;
    }

    // ========== HSV / 色差工具 ==========

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

    private static Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    // 简单的 RGB 欧氏距离（够用了，不需要 Lab ΔE）
    private static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
