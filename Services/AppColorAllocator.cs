// ============================================================================
// AppColorAllocator.cs — 应用专属颜色分配器（静态类）
// 职责：为每个进程名分配稳定且可区分的颜色：
//       自定义颜色(AppColors 表) > 已分配 > 按名称哈希从未用调色板取色；
//       LoadFromDb 预载缓存，SetCustom 写库并更新缓存。
// ============================================================================
using System;                              // Math、Random、Convert
using System.Collections.Generic;          // Dictionary / HashSet
using System.Linq;                         // Sum、Select 等 LINQ 扩展
using System.Windows.Media;                // WPF 的 Color 结构体

namespace TimeActivity.Services;

/// <summary>
/// 应用颜色分配器 — 为新应用自动分配不重复的颜色
/// 预设调色板优先 → 用完随机+色差检查
/// </summary>
/// <remarks>
/// 时间轴上每个应用色块都要有稳定且互相能区分的颜色，所以颜色一旦分配就写进
/// AppColors 表持久化，重启后同一个进程仍是同一个颜色（不能每次随机）。
/// 线程模型：所有公共方法都进 _lock，UI 线程与后台采集线程可安全并发调用。
/// </remarks>
public static class AppColorAllocator
{
    // HSV 均匀分布的 24 色调色板（饱和度 0.75，亮度 0.65，明亮但不刺眼）
    // 静态只读，进程生命周期内只算一次；顺序固定，保证同一台机器上分配顺序可复现
    private static readonly string[] Palette = GeneratePalette();

    // 内存缓存：进程名 → 颜色十六进制字符串
    // 注意：这里的键区分大小写（未指定比较器），而进程名实际都是小写，所以不会出问题
    private static readonly Dictionary<string, string> _cache = new();

    // 缓存上限（2026-08-25 内存优化）：防止字典无界增长。
    // 颜色映射已持久化在 AppColors 表（权威源），缓存仅作内存镜像；
    // 达到上限后新进程只写库、不再进缓存，重启或 LoadFromDb 后仍能恢复完整映射
    private const int MaxCacheSize = 500;

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
        // 24 个位置：24 × 15° = 360°，正好绕色相环一圈
        var colors = new string[24];
        for (int i = 0; i < 24; i++)
        {
            double h = i * 15.0; // 0, 15, 30, ... 345
            var color = HsvToColor(h, 0.75, 0.65);
            // 统一输出 #RRGGBB 大写十六进制，与 AppColors 表里的存储格式保持一致
            colors[i] = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        return colors;
    }

    /// <summary>
    /// 从数据库加载所有应用颜色到内存缓存
    /// </summary>
    public static void LoadFromDb()
    {
        // 与 GetOrAssign 等共用同一把锁：本方法在持锁状态下被内部调用，锁是可重入的
        lock (_lock)
        {
            // 幂等：全进程只预载一次。后续所有读写都基于内存缓存，不再回源
            if (_loaded) return;
            // 理论上首次调用时缓存必为空，清空只是为了重复调用时的语义安全
            _cache.Clear();
            // 把 AppColors 表的全部映射搬进内存；元素是 (进程名, 颜色) 元组
            foreach (var (name, color) in Data.AppColorRepository.GetAll())
            {
                // 后面覆盖前面：表中若存在重复进程名，以最后一条为准
                _cache[name] = color;
            }
            // 注意：_loaded 置 true 之后，表里新增的行不会被自动感知，需重启进程
            _loaded = true;
        }
    }

    /// <summary>
    /// 获取某个应用的颜色。如果有自定义就用自定义，否则自动分配一个不重复的。
    /// </summary>
    public static string GetOrAssign(string processName)
    {
        // 全程持锁：PickAvailableColor 需要读 _cache 里"已占用颜色"，必须原子地读+写
        lock (_lock)
        {
            // 首次调用时把库里的既有映射一次性预载进内存
            LoadFromDb();

            // 已有自定义颜色
            // 命中缓存直接返回 —— 这保证了同一进程名在多次调用、多次启动后颜色稳定不变
            if (_cache.TryGetValue(processName, out var existing))
                return existing;

            // 自动分配
            // 内部会跳过所有已占用的调色板颜色；调色板用完后退化成随机色 + 色差挑选
            var color = PickAvailableColor();
            // 有界写入（2026-08-25）：超限时仅持久化到库（权威源），不进内存镜像，
            // 防止字典无界增长；重启或下次 LoadFromDb 时映射仍可完整恢复
            if (_cache.Count < MaxCacheSize)
                _cache[processName] = color;
            // 库是权威源：无论是否进缓存都要落库，否则重启后该进程会拿到一个新颜色
            Data.AppColorRepository.Set(processName, color);
            // 已知遗留行为：缓存满后同一进程重复调用会重复写库，但返回的颜色可能每次不同
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
            // 用户主动设置的颜色必须即时反映：已存在键直接更新；
            // 新键仅在缓存未满时加入（满则只落库，保持内存有界）
            if (_cache.ContainsKey(processName) || _cache.Count < MaxCacheSize)
                // 覆盖式赋值：自定义颜色可以反复改，最后一次生效
                _cache[processName] = color;
            // 落库持久化：设置页改完色，重启后仍然生效
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
            // 返回副本而不是 _cache.Values 本身：调用方（设置页取色器）可随意遍历/修改，
            // 不会把内部缓存暴露出去造成并发写坏
            return new HashSet<string>(_cache.Values);
        }
    }

    /// <summary>
    /// 挑一个还没被占用的颜色。调色板优先，用完了随机生成 + 色差检查避免颜色太接近。
    /// </summary>
    /// <returns>可用的颜色十六进制字符串</returns>
    private static string PickAvailableColor()
    {
        // 已在 GetOrAssign / SetCustom 的 lock 内，直接读 _cache 避免重复加锁
        // 用 HashSet 去重：库里可能存了多个进程共用同一颜色，去重后比较更快
        var used = new HashSet<string>(_cache.Values);

        // 先从调色板里找一个没被占用的
        // 调色板是确定性来源：前 24 个应用一定能拿到互不相同、色相均匀的颜色
        foreach (var c in Palette)
        {
            if (!used.Contains(c))
                return c; // 按调色板顺序取第一个空位，同一台机器上分配顺序可复现
        }

        // 调色板用完了，随机生成 HSV 颜色 + 色差检查
        var rng = new Random();
        string best = Palette[0];     // 兜底：若循环一轮都没改进（不可能），至少返回一个合法颜色
        double bestMinDist = -1;      // -1 保证第一个候选一定会被采纳

        // 最多试 50 次：够找到"离已用颜色最远"的候选，又不至于在极端情况下卡住
        for (int i = 0; i < 50; i++)
        {
            double h = rng.NextDouble() * 360;        // 色相全环随机
            double s = 0.6 + rng.NextDouble() * 0.25; // 饱和度 0.6~0.85
            double v = 0.55 + rng.NextDouble() * 0.2;  // 亮度 0.55~0.75
            var candidate = HsvToColor(h, s, v);
            var candidateHex = $"#{candidate.R:X2}{candidate.G:X2}{candidate.B:X2}";

            // 找跟所有已用颜色最小色差最大的那个（最不容易撞色）
            double minDist = double.MaxValue;
            foreach (var usedColor in used)
            {
                // 已用颜色可能来自用户手填的非法串，HexToColor 内部有灰色兜底，不会抛
                var usedRgb = HexToColor(usedColor);
                double dist = ColorDistance(candidate, usedRgb);
                if (dist < minDist) minDist = dist;
            }
            // 边界：used 为空（首个应用）时 minDist 仍是 MaxValue，下面立刻命中并 break

            // 保留"最小色差最大"的候选，即离现有颜色群体最远的那个
            if (minDist > bestMinDist)
            {
                bestMinDist = minDist;
                best = candidateHex;
            }

            // 色差够大就直接用，不用再试了
            if (bestMinDist >= 30.0) break;
        }

        // 已知遗留行为：这里只保证"尽量不撞色"，不保证与已用颜色完全不同
        // （50 次采样都可能落在同一色区，或与某个已用颜色恰好同值）
        return best;
    }

    // ========== HSV / 色差工具 ==========
    // 下面三个纯函数无副作用、无共享状态，可在锁内安全调用

    /// <summary>
    /// HSV 转 RGB 颜色。
    /// </summary>
    /// <param name="h">色相 0~360</param>
    /// <param name="s">饱和度 0~1</param>
    /// <param name="v">亮度 0~1</param>
    /// <returns>RGB 颜色</returns>
    private static Color HsvToColor(double h, double s, double v)
    {
        // 色相先归一化到 [0,360)：调用方可能传入 360 或更大的值
        h = h % 360;
        double c = v * s;   // chroma：色度，决定颜色的鲜艳程度
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1)); // 中间分量，随色相做三角波变化
        double m = v - c;   // 明度补偿量，最后加到三个分量上

        // 按色相所在 60° 区间决定 (r,g,b) 里谁是 c、谁是 x、谁是 0
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }        // 红 → 黄
        else if (h < 120) { r = x; g = c; b = 0; }  // 黄 → 绿
        else if (h < 180) { r = 0; g = c; b = x; }  // 绿 → 青
        else if (h < 240) { r = 0; g = x; b = c; }  // 青 → 蓝
        else if (h < 300) { r = x; g = 0; b = c; }  // 蓝 → 品红
        else { r = c; g = 0; b = x; }               // 品红 → 红

        // 分量从 0~1 映射到 0~255；用 Round 而不是截断，减少量化误差导致的偏色
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
        // 0x90A4AE 是 Material 的 Blue Grey 400，作为"未知颜色"的中性灰蓝
        if (string.IsNullOrWhiteSpace(hex)) return Color.FromRgb(0x90, 0xA4, 0xAE);
        // 去掉前导 #；写法上只 TrimStart '#',所以 "# #FF0000" 这类脏数据仍会走到 catch
        hex = hex.TrimStart('#');
        // 只支持 6 位 #RRGGBB；3 位简写与 8 位带透明度的写法都不支持，统一回退灰色
        if (hex.Length != 6) return Color.FromRgb(0x90, 0xA4, 0xAE);
        try
        {
            // 每两位一段按 16 进制解析成一个字节分量
            return Color.FromRgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch
        {
            // 含非十六进制字符（如 "GGGGGG"）时兜底为灰色；吞掉异常保证颜色挑选流程不中断
            return Color.FromRgb(0x90, 0xA4, 0xAE);
        }
    }

    /// <summary>
    /// 计算两个颜色的 RGB 欧氏距离，值越大颜色差异越大。够用了，不需要 Lab ΔE。
    /// </summary>
    private static double ColorDistance(Color a, Color b)
    {
        // 分量差值用 double 计算：byte 相减会先提升为 int，不会溢出
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        // RGB 空间欧氏距离，理论最大约 441（黑白之间）；阈值 30 大约相当于"肉眼能明显区分"
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
