using System;
using System.IO;

namespace TimeActivity.Services;

/// <summary>
/// 开机自启管理 — 通过启动文件夹创建/删除 .lnk 快捷方式
/// 遵循 SRP：只管开机自启的启用/禁用
/// </summary>
public static class AutoStartHelper
{
    private const string ShortcutName = "TimeActivity.lnk";

    /// <summary>
    /// 启用开机自启（创建快捷方式到启动文件夹，带 --minimized 参数）
    /// </summary>
    public static void Enable()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            CreateShortcut(shortcutPath, exePath, "--minimized");
        }
        catch { }
    }

    /// <summary>
    /// 禁用开机自启（删除启动文件夹中的快捷方式）
    /// </summary>
    public static void Disable()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }
        catch { }
    }

    /// <summary>
    /// 查询当前是否已启用开机自启
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            return File.Exists(shortcutPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// 创建 .lnk 快捷方式（通过 WScript.Shell COM）
    /// </summary>
    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.WindowStyle = 1;
        shortcut.Save();
    }
}
