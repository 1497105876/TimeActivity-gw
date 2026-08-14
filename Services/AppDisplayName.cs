using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TimeActivity.Services;

/// <summary>
/// 获取进程的友好显示名（如 taskmgr → 任务管理器，msedge → Microsoft Edge）
/// 通过读取 exe 的 FileVersionInfo.FileDescription 实现
/// </summary>
public static class AppDisplayName
{
    // 进程名→显示名的缓存，忽略大小写
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    // Win32 API：通过进程句柄拿 exe 完整路径（比 MainModule 可靠，UWP/系统进程也能拿）
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    // 打开进程句柄，只需要最低权限
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    // 关闭句柄
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 最低权限标志，足够查询进程路径
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 获取进程的友好显示名（带缓存）。比如 taskmgr → "任务管理器"。
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
    /// 实际解析显示名：找到 exe 路径 → 读 FileVersionInfo → 优先 FileDescription，其次 ProductName。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>友好显示名</returns>
    private static string ResolveDisplayName(string processName)
    {
        try
        {
            string? exePath = GetExePath(processName);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return processName;

            // 读取 exe 的版本信息
            var info = FileVersionInfo.GetVersionInfo(exePath);

            // 优先用 FileDescription（如 "任务管理器"、"Microsoft Edge"）
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();

            // 其次用 ProductName（有些 exe 没设 FileDescription 但有 ProductName）
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                return info.ProductName.Trim();

            // 都没有就用进程名
            return processName;
        }
        catch
        {
            return processName;
        }
    }

    /// <summary>
    /// 通过进程名拿 exe 路径：先试 MainModule，失败了用 Win32 QueryFullProcessImageName 兜底。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>exe 完整路径，找不到返回 null</returns>
    private static string? GetExePath(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return null;

        foreach (var proc in procs)
        {
            // 方法1：MainModule.FileName（大部分进程能用，UWP/系统进程会抛异常）
            try
            {
                string? path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    proc.Dispose();
                    return path;
                }
            }
            catch (Exception ex) { Logger.Error("GetExePathByProcessName MainModule 失败", ex); }

            // 方法2：Win32 QueryFullProcessImageName（权限要求低，UWP/系统进程也能拿）
            try
            {
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder(260);
                        uint size = (uint)sb.Capacity;
                        if (QueryFullProcessImageNameW(hProcess, 0, sb, ref size))
                        {
                            string? result = sb.ToString();
                            if (!string.IsNullOrEmpty(result) && File.Exists(result))
                            {
                                proc.Dispose();
                                return result;
                            }
                        }
                    }
                    finally
                    {
                        // 句柄用完必须关
                        CloseHandle(hProcess);
                    }
                }
            }
            catch (Exception ex) { Logger.Error("QueryFullProcessImageNameW 失败", ex); }

            proc.Dispose();
        }
        return null;
    }
}
