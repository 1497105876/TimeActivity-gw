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
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    // Win32 API：通过 PID 拿 exe 路径（比 MainModule 可靠，权限要求低）
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 获取进程的友好显示名（带缓存）
    /// </summary>
    public static string Get(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return "未知";
        if (processName == "(空闲)") return "空闲";

        lock (_lock)
        {
            if (_cache.TryGetValue(processName, out var cached))
                return cached;
        }

        string displayName = ResolveDisplayName(processName);

        lock (_lock)
        {
            _cache[processName] = displayName;
        }
        return displayName;
    }

    private static string ResolveDisplayName(string processName)
    {
        try
        {
            string? exePath = GetExePath(processName);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return processName;

            var info = FileVersionInfo.GetVersionInfo(exePath);

            // 优先用 FileDescription（如 "任务管理器"、"Microsoft Edge"）
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();

            // 其次用 ProductName
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
    /// 通过进程名拿 exe 路径
    /// </summary>
    private static string? GetExePath(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return null;

        foreach (var proc in procs)
        {
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

            // fallback：QueryFullProcessImageName
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
