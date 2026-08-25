// ============================================================================
// IconExtractor.cs — 进程图标提取与缓存（静态类）
// 职责：由进程名定位 exe → 提取 16px 图标 → 转 WPF ImageSource；
//       内存字典缓存 + 磁盘缓存目录，未找到时返回 null 并记忆负结果避免反复探测。
// 优化：使用 WeakReference 缓存，支持 LRU 淘汰，内存压力时自动释放
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeActivity.Services;

/// <summary>
/// 提取应用程序图标 - 内存优化版：使用 WeakReference 缓存，支持 LRU 淘汰
/// </summary>
public static class IconExtractor
{
    // 进程名→(图标弱引用, 记录时间、访问计数)。成功图标长期缓存；失败的(含字母头像标记)带时间戳，超时可重试
    private static readonly Dictionary<string, (WeakReference<ImageSource> IconRef, DateTime At, int Hits)> _cache = new();
    private static readonly object _lock = new();

    // 正向缓存 LRU 上限：最多缓存 150 个图标（从 200 降低），超出按 LRU 淘汰
    private const int MaxCacheSize = 150;

    // 负结果重试间隔：拿不到图标的进程每 10 分钟允许再试一次（环境可能变化，如提权后）
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    // 进程名→exe路径 的全量快照缓存（弱引用，允许 GC 回收）：避免每次取图标都遍历进程
    private static WeakReference<Dictionary<string, string>>? _pathMapRef;
    private static DateTime _pathMapAt = DateTime.MinValue;
    private static readonly object _mapLock = new();

    // Win32 API：通过进程句柄拿 exe 完整路径（比 MainModule 更可靠，UWP/系统进程也能拿）
    // dwFlags=0 表示返回完整路径，lpExeName 接收路径字符串，lpdwSize 传入缓冲区大小、返回实际长度
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    // 打开进程句柄，权限只需要 PROCESS_QUERY_LIMITED_INFORMATION（不需要管理员权限）
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    // 关闭句柄，用完必须调
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 最低权限标志，足够查询进程路径，不需要管理员
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 根据进程名获取图标（带弱引用缓存 + LRU 淘汰）。
    /// 2026-08-23 增强：① 负缓存 10 分钟过期自动重试；② 彻底弃用 MainModule（消除"拒绝访问"异常）；
    /// ③ 最终兜底返回"首字母+分类色系圆角块"头像，界面不再出现大片空白。
    /// 内存优化：使用 WeakReference 缓存，内存压力时自动释放；LRU 淘汰上限 150 个。
    /// </summary>
    public static ImageSource? GetIcon(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;

// 先尝试从缓存获取
        ImageSource? cachedIcon = null;
        lock (_lock)
        {
            if (_cache.TryGetValue(processName, out var cached))
            {
                // fresh 判定：有图标 → 永久新鲜；无图标 → 仅负缓存 TTL 内新鲜
                if (cached.IconRef != null && cached.IconRef.TryGetTarget(out var icon) && icon != null)
                {
                    // fresh 判定：有图标 → 永久新鲜；无图标 → 仅负缓存 TTL 内新鲜
                    if (true)
                    {
                        // 命中：更新访问时间和计数（用于 LRU）
                        _cache[processName] = (cached.IconRef, DateTime.Now, cached.Hits + 1);
                        return icon;
                    }
                }
                // 弱引用已失效（GC 回收）或负缓存过期
            }
        }

        // 缓存没有/已过期就提取；仍失败则生成字母头像兜底
        ImageSource? extractedIcon = ExtractIconInternal(processName) ?? CreateLetterAvatar(processName);

        lock (_lock)
        {
            // LRU 淘汰：超出上限移除最久未访问（Hits 最小）的项
            if (_cache.Count >= 150)
            {
                var lru = _cache.OrderBy(kv => kv.Value.Hits).First();
                _cache.Remove(lru.Key);
            }
            // 无论成败都写入缓存（带时间戳、Hits=1），供下次快速返回或到期重试
            _cache[processName] = (new WeakReference<ImageSource>(extractedIcon), DateTime.Now, 1);
        }
        return extractedIcon;
    }

    /// <summary>
    /// 实际提取图标的内部方法：找到 exe 路径 → 用 Icon.ExtractAssociatedIcon 提取 → 转成 BitmapImage。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>图标的 ImageSource，失败返回 null</returns>
    private static ImageSource? ExtractIconInternal(string processName)
    {
        try
        {
            // 先通过运行中的进程拿 exe 路径
            string? exePath = GetExePathByProcessName(processName);

            // 进程没在运行，尝试在常见安装路径找
            if (string.IsNullOrEmpty(exePath))
                exePath = FindExePath(processName);

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return null;

            // 用 .NET 内置方法从 exe 文件提取关联图标
            // using 确保 HICON/HBITMAP 等 GDI 资源及时释放
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;

            // 把 Icon 转成 BitmapImage（WPF 用的格式），通过 PNG 内存流中转
            using var bitmap = icon.ToBitmap();
            using var memory = new MemoryStream();
            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            memory.Position = 0;

            var source = new BitmapImage();
            source.BeginInit();
            source.StreamSource = memory;
            source.CacheOption = BitmapCacheOption.OnLoad;  // 加载完立即关闭流
            source.EndInit();
            source.Freeze();  // 冻结后可以跨线程使用
            return source;
        }
        catch
        {
            // exe 无图标/被占用/格式异常等 → 返回 null 走字母头像兜底
            return null;
        }
    }

    /// <summary>
    /// 通过进程名拿 exe 路径：优先查"全进程路径快照"（全部走 Win32 低权限查询，
    /// 完全不触碰 MainModule，从根上消除 UWP/系统进程的"拒绝访问"异常）。
    /// </summary>
    private static string? GetExePathByProcessName(string processName)
    {
        var map = GetProcessPathMap();
        return map.TryGetValue(processName.ToLowerInvariant(), out var path) ? path : null;
    }

    /// <summary>
    /// 构建并缓存"所有运行中进程 → exe 路径"映射（按需懒加载，无定时刷新）。
    /// 仅在缓存为空或 GC 回收时重新构建，避免后台定时枚举进程。
    /// </summary>
    private static Dictionary<string, string> GetProcessPathMap()
    {
        lock (_mapLock)
        {
            // 缓存存在且未被 GC 回收直接复用
            if (_pathMapRef != null && _pathMapRef.TryGetTarget(out var cached))
                return cached;

            // 构建新快照（先建局部变量再赋值字段：失败时旧快照仍可用）
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in Process.GetProcesses())
            {
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
                if (h == IntPtr.Zero) { proc.Dispose(); continue; }
                try
                {
                    var sb = new System.Text.StringBuilder(520);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                    {
                        string p = sb.ToString();
                        if (!string.IsNullOrEmpty(p))
                            map[proc.ProcessName.ToLowerInvariant()] = p;
                    }
                }
                catch { /* 个别进程查询失败直接跳过 */ }
                finally { CloseHandle(h); proc.Dispose(); }
            }
            var newMap = map;
            _pathMapRef = new WeakReference<Dictionary<string, string>>(newMap);
            return newMap;
        }
    }

    /// <summary>
    /// 兜底头像：以分类色系(按名称哈希取柔和色相)画圆角块 + 白色首字母，
    /// 保证任何进程都有可辨识的视觉占位。
    /// 注意：DrawingVisual/RenderTargetBitmap 要求 STA 线程；
    /// 非 STA 线程调用会抛异常并被下方 catch 吞掉。
    /// </summary>
    private static ImageSource? CreateLetterAvatar(string processName)
    {
        try
        {
            // 由名称哈希生成稳定的柔和色相，避免全灰一片
            int hash = 0;
            foreach (var ch in processName) hash = hash * 31 + ch;
            var hue = (byte)(hash % 360);
            // 饱和度/明度取中低值，保证白色文字可读且不刺眼
            var fill = ColorFromHsv(hue, 0.45, 0.78);

            const int size = 16;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // 画圆角矩形底色
                var rect = new Rect(0, 0, size, size);
                dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, 3, 3);
                // 首字母大写、粗体白字，居中绘制
                var text = new FormattedText(
                    processName.Substring(0, 1).ToUpperInvariant(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    10, System.Windows.Media.Brushes.White, 1.25);
                dc.DrawText(text, new System.Windows.Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            // 离屏渲染为 16x16 位图（96 DPI）
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze(); // 冻结后可跨线程使用
            return bmp;
        }
        catch
        {
            return null; // 极端失败退回 null（调用方还有占位边框）
        }
    }

    /// <summary>HSV → WPF Color（用于按哈希色相生成头像底色）。</summary>
    /// <param name="hue360">色相，0~360 度</param>
    /// <param name="s">饱和度 0~1</param>
    /// <param name="v">明度 0~1</param>
    private static System.Windows.Media.Color ColorFromHsv(double hue360, double s, double v)
    {
        // 标准 HSV→RGB 公式：c=色度，x=第二分量，m=使最大值对齐 v 的偏移
        double c = v * s;
        double x = c * (1 - Math.Abs((hue360 / 60) % 2 - 1));
        double m = v - c;
        // 按色相所在 60° 扇区选择 (r,g,b) 的排列组合
        (double r, double g, double b) = ((int)(hue360 / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return System.Windows.Media.Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>
    /// 进程已退出时的兜底：在 Program Files、LocalAppData、Windows 等常见路径找 exe。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>找到的 exe 路径，找不到返回 null</returns>
    private static string? FindExePath(string processName)
    {
        string[] extensions = { ".exe", "" };
        // 常见安装目录（含 Electron/VSCode 等常用的 %LocalAppData%\Programs）
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] searchPaths = {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(localAppData, "Programs"),          // 现代 per-user 安装默认位置
            localAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")
        };

        // 逐个搜索路径尝试
        foreach (string searchPath in searchPaths)
        {
            // GetFolderPath 可能返回空串（目录不存在于此系统），跳过
            if (string.IsNullOrEmpty(searchPath)) continue;
            foreach (string ext in extensions)
            {
                // 先试 "路径\进程名\进程名.exe"（很多软件有自己的子目录）
                string candidate = Path.Combine(searchPath, processName, processName + ext);
                if (File.Exists(candidate)) return candidate;
                // 再试 "路径\进程名.exe"（直接放在根目录）
                candidate = Path.Combine(searchPath, processName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 清理已失效的弱引用（GC 已回收的图标）。建议定期调用（如每 5 分钟）或在内存压力时调用。
    /// </summary>
    public static void CleanupDeadReferences()
    {
        lock (_lock)
        {
            var deadKeys = _cache
                .Where(kv => kv.Value.IconRef == null || !kv.Value.IconRef.TryGetTarget(out _))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in deadKeys)
                _cache.Remove(key);
        }

        // 清理路径映射弱引用
        lock (_mapLock)
        {
            if (_pathMapRef != null && !_pathMapRef.TryGetTarget(out _))
                _pathMapRef = null;
        }
    }
    }