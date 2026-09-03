// ============================================================================
// AppDisplayName.cs — 进程友好名解析（静态类）
// 职责：由进程名（如 chrome、devenv）解析出用户友好的显示名（如 "Google Chrome"、"Visual Studio"）
// 实现：读取 exe 的 FileVersionInfo，优先用 FileDescription，次选 ProductName，兜底用进程名
// 优化：使用低权限 Win32 API 避免 MainModule 访问异常；缓存结果避免重复解析
// ============================================================================
using System;                              // StringComparison、IntPtr、Exception
using System.Collections.Generic;          // Dictionary
using System.Diagnostics;                  // Process、FileVersionInfo
using System.IO;                           // File.Exists
using System.Runtime.InteropServices;      // DllImport、Out、CharSet
using System.Text;                         // StringBuilder（Win32 路径缓冲区）
using TimeActivity.Data;                   // 预留：若日后改为从库里读自定义显示名
using TimeActivity.Helpers;                // Logger

namespace TimeActivity.Services;

/// <summary>
/// 进程友好名解析器（静态类，线程安全）
/// </summary>
/// <remarks>
/// 时间轴和统计报表里展示的是"Google Chrome"这类可读名，而不是 "chrome"，
/// 所以每次遇到新进程都要解析一次 exe 的版本信息；解析涉及进程枚举与文件 IO，
/// 结果必须缓存，否则后台采集线程会被拖慢。
/// </remarks>
public static class AppDisplayName
{
    // 进程名 → 友好显示名的缓存（忽略大小写）
    // Windows 进程名本身不区分大小写，用 OrdinalIgnoreCase 避免重复解析
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    // 保护 _cache 的锁；解析过程（慢）放在锁外，只有字典读写在锁内
    private static readonly object _lock = new();

    // 缓存上限（2026-08-25 内存优化）：进程名集合自然有限，超限整体清空重建，
    // 热点进程（用户常用软件）会很快重新填充，防止字典无界增长
    private const int MaxCacheSize = 300;

    // Win32 API：通过进程句柄拿 exe 完整路径（比 MainModule 更可靠，UWP/系统进程也能拿）
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
    // 用这个标志而不是 PROCESS_ALL_ACCESS，是为了对系统保护进程/高完整性进程也能拿到路径
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 获取进程的友好显示名（带缓存）。
    /// 例如："chrome" → "Google Chrome"、"devenv" → "Visual Studio"、"taskmgr" → "任务管理器"
    /// </summary>
    /// <param name="processName">进程名（不含 .exe 后缀）</param>
    /// <returns>友好显示名，找不到时返回进程名本身</returns>
    public static string Get(string processName)
    {
        // 空进程名：拿不到前台窗口时的兜底文案，与 TrackingEngine 的空值口径对应
        if (string.IsNullOrEmpty(processName)) return "未知";
        // "(空闲)" 是追踪引擎约定的空闲态哨兵进程名，直接翻译成中文，不必去查 exe
        if (processName == "(空闲)") return "空闲";

        // 先查缓存
        lock (_lock)
        {
            // 命中即返回：绝大多数调用走这条路，零 IO、零进程枚举
            if (_cache.TryGetValue(processName, out var cached))
                return cached;
        }

        // 缓存没有就解析（可能涉及进程枚举 + 读 exe 版本信息，较慢，故意放在锁外，
        // 避免一个进程解析慢就阻塞其他线程的缓存查询）
        string displayName = ResolveDisplayName(processName);

        lock (_lock)
        {
            // 超限防御：仅当新键即将写入且缓存已满时整体重建（避免只读命中被清空）
            // 直接 Clear 而不是 LRU 淘汰：进程名集合本身有限，重建代价可接受
            if (_cache.Count >= MaxCacheSize && !_cache.ContainsKey(processName))
                _cache.Clear();
            // 写回缓存；解析失败时存的就是进程名本身，下次直接命中，不会反复重试解析
            _cache[processName] = displayName;
        }
        return displayName;
    }

    /// <summary>
    /// 实际解析显示名：找到 exe 路径 → 读 FileVersionInfo → 取 FileDescription/ProductName
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>友好显示名</returns>
    private static string ResolveDisplayName(string processName)
    {
        // 第一步：定位该进程名的 exe 路径
        string? exePath = GetExePath(processName);
        // 拿不到路径或文件已不存在（进程刚退出）→ 只能用进程名本身，属于正常降级
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return processName;

        // 读取 exe 的版本信息
        try
        {
            // GetVersionInfo 会读 PE 资源节；文件被占用/损坏/权限不足都可能抛异常
            var info = FileVersionInfo.GetVersionInfo(exePath);

            // 优先用 FileDescription（如 "任务管理器" 而非 "taskmgr"）
            // 这是大多数正规软件填的字段，通常就是用户认识的产品名
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim(); // Trim 去掉版本信息里常见的首尾空格

            // 其次用 ProductName（有些 exe 没设 FileDescription 但有 ProductName）
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                return info.ProductName.Trim();

            // 都没有就用进程名
            return processName;
        }
        catch (Exception ex)
        {
            // 解析失败只记日志：显示名是锦上添花的展示信息，绝不能让采集流程崩掉
            Logger.Error($"解析显示名失败: {processName}", ex);
            // 兜底返回进程名，调用方无需判空
            return processName;
        }
    }

    /// <summary>
    /// 通过进程名拿 exe 路径：使用低权限 Win32 API 遍历同名进程，
    /// 完全避免 MainModule 访问异常。
    /// </summary>
    private static string? GetExePath(string processName)
    {
        // 按进程名枚举（可能多开，返回多个实例）；GetProcessesByName 本身较慢，靠上层缓存挡住
        var procs = Process.GetProcessesByName(processName);
        // 进程没在跑（已退出/已采集到历史数据）→ 无从解析，返回 null
        if (procs.Length == 0) return null;

        foreach (var proc in procs)
        {
            // 直接使用低权限 Win32 API，避开 MainModule 访问异常
            // proc.MainModule 对跨会话/受保护进程会抛 Win32Exception，本项目一律不用它
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
            // 打开失败（进程已退出、权限不足、受保护进程）→ 释放 Process 对象，换下一个实例
            if (h == IntPtr.Zero) { proc.Dispose(); continue; }
            try
            {
                // 520 字符缓冲区：足够放下 MAX_PATH(260) 及常见长路径；
                // capacity 同时作为传入的缓冲区大小参数（字符数）
                var sb = new System.Text.StringBuilder(520);
                uint size = (uint)sb.Capacity;
                // dwFlags 传 0 = Win32 路径格式；API 会回写实际长度到 size
                if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                {
                    string p = sb.ToString();
                    if (!string.IsNullOrEmpty(p))
                    {
                        // 拿到路径即可返回；finally 里还会再 Dispose 一次（重复 Dispose 对 Process 是安全的）
                        proc.Dispose();
                        return p;
                    }
                }
                // API 返回 false 或路径为空：落到 finally，继续试下一个同名进程
            }
            catch { /* 个别进程查询失败直接跳过 */ }
            finally { CloseHandle(h); proc.Dispose(); }
        }
        // 所有同名实例都拿不到路径
        return null;
    }
}