// ============================================================================
// IconExtractor.cs — 进程图标提取与缓存（静态类）
// 职责：由进程名定位 exe → 提取 16px 图标 → 转 WPF ImageSource；
//       内存字典缓存 + 磁盘缓存目录，未找到时返回 null 并记忆负结果避免反复探测。
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
/// 提取应用程序图标
/// </summary>
public static class IconExtractor
{
    // 进程名→图标的缓存，同一进程只提取一次
    private static readonly Dictionary<string, ImageSource?> _cache = new();
    private static readonly object _lock = new();

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
    /// 根据进程名获取图标（带缓存，同一进程名只提取一次）。
    /// </summary>
    /// <param name="processName">进程名（不含 .exe 后缀）</param>
    /// <returns>图标的 ImageSource，提取失败返回 null</returns>
    public static ImageSource? GetIcon(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;

        // 先查缓存
        lock (_lock)
        {
            if (_cache.TryGetValue(processName, out var cached))
                return cached;
        }

        // 缓存没有就提取，然后存入缓存
        ImageSource? icon = ExtractIconInternal(processName);

        lock (_lock)
        {
            _cache[processName] = icon;
        }
        return icon;
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
            return null;
        }
    }

    /// <summary>
    /// 通过进程名拿 exe 路径：先试 MainModule（大部分进程能用），
    /// 失败了用 Win32 QueryFullProcessImageName（UWP/系统进程/权限不足时兜底）。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>exe 完整路径，找不到返回 null</returns>
    private static string? GetExePathByProcessName(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return null;

        foreach (var proc in procs)
        {
            // 方法1：MainModule.FileName（大部分进程能用，但 UWP/系统进程会抛异常）
            try
            {
                string? path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    proc.Dispose();
                    return path;
                }
            }
            catch
            {
                // MainModule 访问失败（UWP/系统进程/权限不足），试方法2
            }

            // 方法2：Win32 QueryFullProcessImageName（只需要 PROCESS_QUERY_LIMITED_INFORMATION，普通用户权限就行）
            try
            {
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder(260);
                        uint size = (uint)sb.Capacity;
                        // 返回 true 表示成功拿到路径
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
                        // 句柄用完必须关，否则泄漏
                        CloseHandle(hProcess);
                    }
                }
            }
            catch (Exception ex) { Logger.Error("IconExtractor GetExePath 失败", ex); }

            proc.Dispose();
        }
        return null;
    }

    /// <summary>
    /// 进程已退出时的兜底：在 Program Files、LocalAppData、Windows 等常见路径找 exe。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>找到的 exe 路径，找不到返回 null</returns>
    private static string? FindExePath(string processName)
    {
        string[] extensions = { ".exe", "" };
        // 常见安装目录
        string[] searchPaths = {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")
        };

        // 逐个搜索路径尝试
        foreach (string searchPath in searchPaths)
        {
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
}
