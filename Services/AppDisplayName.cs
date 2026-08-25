// ============================================================================
// AppDisplayName.cs — 进程友好名解析（静态类）
// 职责：由进程名（如 chrome、devenv）解析出用户友好的显示名（如 "Google Chrome"、"Visual Studio"）
// 实现：读取 exe 的 FileVersionInfo，优先用 FileDescription，次选 ProductName，兜底用进程名
// 优化：使用低权限 Win32 API 避免 MainModule 访问异常；缓存结果避免重复解析
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TimeActivity.Data;
using TimeActivity.Helpers;

namespace TimeActivity.Services;

/// <summary>
/// 进程友好名解析器（静态类，线程安全）
/// </summary>
public static class AppDisplayName
{
    // 进程名 → 友好显示名的缓存（忽略大小写）
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

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
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 获取进程的友好显示名（带缓存）。
    /// 例如："chrome" → "Google Chrome"、"devenv" → "Visual Studio"、"taskmgr" → "任务管理器"
    /// </summary>
    /// <param name="processName">进程名（不含 .exe 后缀）</param>
    /// <returns>友好显示名，找不到时返回进程名本身</returns>
    public static string Get(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return "未知";
        if (processName == "(空闲)") return "空闲";

        // 先查缓存
        lock (_lock)
        {
            if (_cache.TryGetValue(processName, out var cached))
                return cached;
        }

        // 缓存没有就解析，然后存入缓存
        string displayName = ResolveDisplayName(processName);

        lock (_lock)
        {
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
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return processName;

        // 读取 exe 的版本信息
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);

            // 优先用 FileDescription（如 "任务管理器" 而非 "taskmgr"）
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();

            // 其次用 ProductName（有些 exe 没设 FileDescription 但有 ProductName）
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                return info.ProductName.Trim();

            // 都没有就用进程名
            return processName;
        }
        catch (Exception ex)
        {
            Logger.Error($"解析显示名失败: {processName}", ex);
            return processName;
        }
    }

    /// <summary>
    /// 通过进程名拿 exe 路径：使用低权限 Win32 API 遍历同名进程，
    /// 完全避免 MainModule 访问异常。
    /// </summary>
    private static string? GetExePath(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return null;

        foreach (var proc in procs)
        {
            // 直接使用低权限 Win32 API，避开 MainModule 访问异常
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
                    {
                        proc.Dispose();
                        return p;
                    }
                }
            }
            catch { /* 个别进程查询失败直接跳过 */ }
            finally { CloseHandle(h); proc.Dispose(); }
        }
        return null;
    }
}