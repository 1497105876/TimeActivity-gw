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
    // 保护 _cache 的锁（读和写都加锁；Dictionary 非线程安全，并发读写会损坏内部结构）
    private static readonly object _lock = new();

    // Win32 API：通过进程句柄拿 exe 完整路径（比 MainModule 可靠，UWP/系统进程也能拿）
    // dwFlags=0 → 返回 Win32 路径格式；lpExeName 为接收缓冲区；
    // lpdwSize 双向参数：传入容量（字符数），返回实际写入长度（不含 \0）。
    // 返回 false 表示失败（权限不足/缓冲区过小/进程已退出）
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    // 打开进程句柄，只需要最低权限
    // 返回进程句柄，失败返回 IntPtr.Zero（用 Marshal.GetLastWin32Error 可取原因，
    // 此处未开启有效错误捕获，仅判零）；句柄必须配对 CloseHandle 释放
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    // 关闭句柄
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 最低权限标志，足够查询进程路径
    // 仅此权限即可配合 QueryFullProcessImageNameW，普通用户权限即可成功（无需管理员）
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 获取进程的友好显示名（带缓存）。比如 taskmgr → "任务管理器"。
    /// </summary>
    /// <param name="processName">进程名（不含 .exe 后缀）</param>
    /// <returns>友好显示名，找不到时返回进程名本身</returns>
    public static string Get(string processName)
    {
        // 空名与系统空闲占位直接短路返回
        if (string.IsNullOrEmpty(processName)) return "未知";
        if (processName == "(空闲)") return "空闲";

        // 先查缓存
        lock (_lock)
        {
            if (_cache.TryGetValue(processName, out var cached))
                return cached;
        }

        // 缓存没有就解析，然后存入缓存
        // 注意：解析在锁外进行 —— 并发时可能重复解析同一名字，
        // 结果一致属良性竞争，换来的是不长时间持锁（解析含磁盘 IO）
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
            // 第一步：定位该进程名的 exe 完整路径
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
            // 文件被占用/无读权限等异常一律退回进程名，绝不影响调用方
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
        // 同名进程可能多个实例，逐个尝试直到拿到可用路径
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
                // 只申请最低查询权限；bInheritHandle=false 句柄不被子进程继承
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        // MAX_PATH(260) 容量；超长路径会失败（不重试更大缓冲，可接受）
                        var sb = new System.Text.StringBuilder(260);
                        // size 双向：传容量、回实际长度
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
