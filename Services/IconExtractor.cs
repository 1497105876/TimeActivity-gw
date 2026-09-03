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
    // 进程名→(图标弱引用, 记录时间、访问计数)。仅缓存"真实提取到的图标"（弱引用+LRU）；
    // 提取失败的进程不进本缓存，改记到下方 _negativeCache（2026-09-02 负缓存重构）
    /// <summary>进程名 → 图标缓存项的字典；只缓存"真实提取到的图标"，弱引用允许 GC 回收。</summary>
    private static readonly Dictionary<string, (WeakReference<ImageSource> IconRef, DateTime At, int Hits)> _cache = new();
    /// <summary>保护 _cache 与 _negativeCache 访问的互斥锁：Dictionary 非线程安全，读改写都在锁内进行。</summary>
    private static readonly object _lock = new();

    // 正向缓存 LRU 上限：最多缓存 150 个图标（从 200 降低），超出按 LRU 淘汰
    /// <summary>主缓存容量上限：达到 150 项时，按"最近最少使用"淘汰最旧的一个再写入。</summary>
    private const int MaxCacheSize = 150;

    // 负结果重试间隔：拿不到图标的进程每 10 分钟允许再试一次（环境可能变化，如提权后）
    /// <summary>负缓存 TTL：距上次提取失败不足 10 分钟的进程不再探测，过期后才允许重试。</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    // 负缓存（2026-09-02 修复）：进程名 → 最近一次提取失败的时间。
    // TTL 内直接用字母头像不重复探测；过期后允许重新提取。随 CleanupDeadReferences 定期清理过期项。
    /// <summary>进程名 → 最近一次"真实图标提取失败"时刻。TTL 内命中即跳过探测，直接返回字母头像。</summary>
    private static readonly Dictionary<string, DateTime> _negativeCache = new();

    // 进程名→exe路径 的全量快照缓存（弱引用，允许 GC 回收）：避免每次取图标都遍历进程
    /// <summary>全进程 → exe 路径快照的弱引用；被 GC 回收后下次 GetIcon 会重建（见 GetProcessPathMap）。</summary>
    private static WeakReference<Dictionary<string, string>>? _pathMapRef;
    /// <summary>路径快照的构建时间戳（预留字段，当前代码不读取）。</summary>
    private static DateTime _pathMapAt = DateTime.MinValue;
    /// <summary>保护 _pathMapRef 的互斥锁，防止多线程并发重建快照。</summary>
    private static readonly object _mapLock = new();

    // Win32 API：通过进程句柄拿 exe 完整路径（比 MainModule 更可靠，UWP/系统进程也能拿）
    // dwFlags=0 表示返回完整路径，lpExeName 接收路径字符串，lpdwSize 传入缓冲区大小、返回实际长度
    /// <summary>按进程句柄查询 exe 完整路径（kernel32.dll 宽字符版）。失败时返回 false 并置 SetLastError。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    // 打开进程句柄，权限只需要 PROCESS_QUERY_LIMITED_INFORMATION（不需要管理员权限）
    /// <summary>以指定权限打开进程句柄（kernel32.dll）。本类只用 PROCESS_QUERY_LIMITED_INFORMATION 查询路径。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    // 关闭句柄，用完必须调
    /// <summary>关闭 OpenProcess 打开的句柄（kernel32.dll）。不调用会造成内核句柄泄漏。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 最低权限标志，足够查询进程路径，不需要管理员
    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION 权限位：无需提权即可查询进程路径/基本信息的最小权限。</summary>
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 根据进程名获取图标（弱引用缓存 + LRU 淘汰 + 负缓存 TTL）。
    /// 2026-09-02 修复（检查报告 1.3/1.4）：原实现把"字母头像兜底"也写入图标缓存，
    /// 导致负缓存 10 分钟 TTL 重试机制完全失效（命中判断恒真）。现拆分：
    ///   - 主缓存 _cache：只存"真实提取到的图标"（弱引用 + LRU 上限）；
    ///   - 负缓存 _negativeCache：记录提取失败的进程与时间，TTL 内直接用字母头像不重复探测，
    ///     过期后允许重新提取（环境变化如提权/文件就绪后可拿到真图标）。
    /// </summary>
    public static ImageSource? GetIcon(string processName)
    {
        // 空名直接返回 null（调用方通常自己画占位），不落任何缓存
        if (string.IsNullOrEmpty(processName)) return null;

        // 1. 主缓存命中（弱引用存活）→ 更新 LRU 后直接返回
        lock (_lock)
        {
            // 命中条件：缓存里有该项，且弱引用目标仍存活（未被 GC 回收）
            if (_cache.TryGetValue(processName, out var cached) &&
                cached.IconRef != null && cached.IconRef.TryGetTarget(out var icon) && icon != null)
            {
                // 命中：更新访问时间与计数（LRU 依据）
                _cache[processName] = (cached.IconRef, DateTime.Now, cached.Hits + 1);
                return icon;
            }
        }

        // 2. 负缓存检查：TTL 内提取失败的进程不再重复探测（省 IO），直接字母头像
        bool recentlyFailed;
        lock (_lock)
        {
            // 若该进程在 NegativeTtl 内失败过 → 判定为"最近失败过"，本轮跳过真实提取
            recentlyFailed = _negativeCache.TryGetValue(processName, out var failedAt)
                             && DateTime.Now - failedAt < NegativeTtl;
        }

        ImageSource? extractedIcon = null;
        if (!recentlyFailed)
            extractedIcon = ExtractIconInternal(processName);

        if (extractedIcon != null)
        {
            // 3a. 提取成功 → 存入主缓存（LRU 淘汰后写入），并清除该进程的负缓存记录（若有）
            lock (_lock)
            {
                // 容量已满先按 LRU 淘汰"最久没被访问"的一项，为新图标腾位置
                if (_cache.Count >= MaxCacheSize)
                {
                    // At 最小 = 最久未命中；OrderBy+First 取它作牺牲品
                    var lru = _cache.OrderBy(kv => kv.Value.At).First();
                    _cache.Remove(lru.Key);
                }
                // 包装成弱引用存入，记录写入时间与初始命中次数 1
                _cache[processName] = (new WeakReference<ImageSource>(extractedIcon), DateTime.Now, 1);
                // 成功后即清负缓存：不再等待 CleanupDeadReferences 周期清理（语义完整，避免
                // 主缓存弱引用被 GC 回收后误走旧失败记录）——虽旧记录已过期无害，但留着无意义
                _negativeCache.Remove(processName);
            }
        }
        else
        {
            // 3b. 提取失败 → 记负缓存（TTL 过后允许重试），兜底字母头像保证界面不空白
            lock (_lock)
            {
                // 记录本次失败时刻，作为 TTL 计时起点
                _negativeCache[processName] = DateTime.Now;
            }
            // 返回字母头像占位，保证界面不出现空白格
            extractedIcon = CreateLetterAvatar(processName);
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

            // 路径最终仍为空，或 exe 文件已被删除（进程退出与查找之间有竞态）→ 视为提取失败
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return null;

            // 用 .NET 内置方法从 exe 文件提取关联图标
            // using 确保 HICON/HBITMAP 等 GDI 资源及时释放
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            // 该 exe 没声明图标时返回 null（很多命令行工具/服务进程无图标）
            if (icon == null) return null;

            // 把 Icon 转成 BitmapImage（WPF 用的格式），通过 PNG 内存流中转
            using var bitmap = icon.ToBitmap();
            // PNG 内存流作为中转：保存→回绕到流头→让 BitmapImage 从头解码
            using var memory = new MemoryStream();
            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            // 回绕指针：BeginInit/StreamSource 从当前位置开始读，不重置会解出空白图
            memory.Position = 0;

            var source = new BitmapImage();
            // BeginInit~EndInit 之间配置 BitmapImage（延迟到 EndInit 才真正解码）
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
        // 先取（或重建）进程→路径快照，再做单次字典反查
        var map = GetProcessPathMap();
        // 键统一存小写，查询侧同样转小写保证命中；查不到返回 null
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
            // 键用忽略大小写的比较器：Windows 文件名不区分大小写，避免同进程名大小写变体各存一份
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 遍历当前全部进程（Process.GetProcesses 返回的数组需要逐个 Dispose）
            foreach (var proc in Process.GetProcesses())
            {
                // 以最低权限打开进程句柄（PROCESS_QUERY_LIMITED_INFORMATION 无需管理员即可查路径）
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
                // 打开失败（进程恰在退出/权限不足）→ 释放 Process 对象后跳过该进程
                if (h == IntPtr.Zero) { proc.Dispose(); continue; }
                try
                {
                    // 520 字符足够容纳 Windows 最大路径长度（旧版上限 260，现代路径可达 32K，520 覆盖绝大多数场景）
                    var sb = new System.Text.StringBuilder(520);
                    // 传入缓冲区容量；调用后 size 被改写为实际路径长度（含 \0）
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                    {
                        string p = sb.ToString();
                        // 个别进程路径为空（如正在卸载的进程）就不登记，避免键→空串
                        if (!string.IsNullOrEmpty(p))
                            // 进程名统一转小写作键，路径按原样存值
                            map[proc.ProcessName.ToLowerInvariant()] = p;
                    }
                }
                catch { /* 个别进程查询失败直接跳过 */ }
                finally { CloseHandle(h); proc.Dispose(); }
            }
            var newMap = map;
            // 用弱引用持住快照：内存紧张时允许 GC 回收，下次使用时重建即可
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
            // 多项式滚动哈希：把名字每个字符加权累加，同名字每次算出同一数值
            int hash = 0;
            foreach (var ch in processName) hash = hash * 31 + ch;
            // 取模 360 得到 0~359 的色相角，保证每个进程名颜色稳定可复现
            var hue = (byte)(hash % 360);
            // 饱和度/明度取中低值，保证白色文字可读且不刺眼
            var fill = ColorFromHsv(hue, 0.45, 0.78);

            // 头像固定 16×16（与 UI 列表图标尺寸一致）
            const int size = 16;
            // DrawingVisual 作为离屏绘制画布（需 STA 线程，失败由外层 catch 兜底）
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
                // 文字在圆角块内水平/垂直居中（按实际文字宽高偏移）
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
        // 扇区编号 = floor(hue/60)：0~4 显式列出，扇区 5（300°~360°）落入默认分支
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
        // 两种扩展名变体都试：标准 ".exe" 与"无扩展名"（极少数注册为无扩展的应用）
        string[] extensions = { ".exe", "" };
        // 常见安装目录（含 Electron/VSCode 等常用的 %LocalAppData%\Programs）
        // 先取用户级 LocalAppData 备用，下面用它拼 per-user 安装目录
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // 候选根目录集合：按命中概率排序（Program Files → x86 → per-user → 系统目录）
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
        // 全部根目录都找不到 → 返回 null，由调用方走字母头像兜底
        return null;
    }

    /// <summary>
    /// 清理已失效的弱引用（GC 已回收的图标）。建议定期调用（如每 5 分钟）或在内存压力时调用。
    /// </summary>
    public static void CleanupDeadReferences()
    {
        lock (_lock)
        {
            // 先筛出"图标已被 GC 回收"的键，收集成独立列表再删（不能边遍历边删字典）
            var deadKeys = _cache
                // 弱引用目标已死亡（TryGetTarget 返回 false）即为失效项
                .Where(kv => kv.Value.IconRef == null || !kv.Value.IconRef.TryGetTarget(out _))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in deadKeys)
                _cache.Remove(key);

            // 清理已过期的负缓存条目（2026-09-02）：TTL 过后移除，允许下次 GetIcon 重新探测；
            // 同时防止负缓存字典随失败进程数无界增长
            var expiredNegatives = _negativeCache
                // 距上次失败已超过 NegativeTtl 的条目视为过期
                .Where(kv => DateTime.Now - kv.Value >= NegativeTtl)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expiredNegatives)
                _negativeCache.Remove(key);
        }

        // 清理路径映射弱引用
        lock (_mapLock)
        {
            // 快照已被 GC 回收则把引用也置空，让下次 GetProcessPathMap 从零重建
            if (_pathMapRef != null && !_pathMapRef.TryGetTarget(out _))
                _pathMapRef = null;
        }
    }
    }