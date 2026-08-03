using System;
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
    [DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 根据进程名获取图标
    /// </summary>
    public static ImageSource? GetIcon(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;

        try
        {
            // 尝试获取进程路径
            var proc = Process.GetProcessesByName(processName).FirstOrDefault();
            string? exePath = null;

            if (proc != null)
            {
                exePath = proc.MainModule?.FileName;
                proc.Dispose();
            }

            // 如果进程没在运行，尝试常见路径
            if (string.IsNullOrEmpty(exePath))
            {
                exePath = FindExePath(processName);
            }

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
    /// 尝试在常见路径找到 exe
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
