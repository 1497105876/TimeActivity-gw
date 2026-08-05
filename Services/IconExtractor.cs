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
    private static readonly Dictionary<string, ImageSource?> _cache = new();
    private static readonly object _lock = new();

    // Win32 API：通过 PID 直接拿 exe 完整路径（比 MainModule 更可靠）
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        [Out] System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 根据进程名获取图标（带缓存，同一进程名只提取一次）
    /// </summary>
    public static ImageSource? GetIcon(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(processName, out var cached))
                return cached;
        }

        ImageSource? icon = ExtractIconInternal(processName);

        lock (_lock)
        {
            _cache[processName] = icon;
        }
        return icon;
    }

    private static ImageSource? ExtractIconInternal(string processName)
    {
        try
        {
            string? exePath = GetExePathByProcessName(processName);

            // 进程没在运行，尝试常见路径
            if (string.IsNullOrEmpty(exePath))
                exePath = FindExePath(processName);

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return null;

            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;

            // 转换为 BitmapSource
            var bitmap = icon.ToBitmap();
            var memory = new MemoryStream();
            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            memory.Position = 0;

            var source = new BitmapImage();
            source.BeginInit();
            source.StreamSource = memory;
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 通过进程名拿 exe 路径：先试 MainModule，失败了用 QueryFullProcessImageName
    /// </summary>
    private static string? GetExePathByProcessName(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return null;

        foreach (var proc in procs)
        {
            try
            {
                // 方法1：MainModule.FileName（大部分进程能用）
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

            // 方法2：QueryFullProcessImageName（只需要 PROCESS_QUERY_LIMITED_INFORMATION，权限要求低）
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
            catch { }

            proc.Dispose();
        }
        return null;
    }

    /// <summary>
    /// 尝试在常见路径找到 exe（进程已退出时的 fallback）
    /// </summary>
    private static string? FindExePath(string processName)
    {
        string[] extensions = { ".exe", "" };
        string[] searchPaths = {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")
        };

        foreach (string searchPath in searchPaths)
        {
            if (string.IsNullOrEmpty(searchPath)) continue;
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(searchPath, processName, processName + ext);
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(searchPath, processName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
