using System;
using System.IO;

namespace TimeActivity.Services;

/// <summary>
/// 开机自启管理 — 通过启动文件夹创建/删除 .lnk 快捷方式
/// 遵循 SRP：只管开机自启的启用/禁用
/// </summary>
public static class AutoStartHelper
{
    // 快捷方式文件名
    private const string ShortcutName = "TimeActivity.lnk";

    /// <summary>
    /// 启用开机自启：在启动文件夹创建快捷方式，带 --minimized 参数启动后最小化到托盘。
    /// </summary>
    public static void Enable()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            // 获取当前 exe 的完整路径
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            CreateShortcut(shortcutPath, exePath, "--minimized");
        }
        catch (Exception ex) { Logger.Error("启用开机自启失败", ex); }
    }

    /// <summary>
    /// 禁用开机自启：删除启动文件夹中的快捷方式。
    /// </summary>
    public static void Disable()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }
        catch (Exception ex) { Logger.Error("禁用开机自启失败", ex); }
    }

    /// <summary>
    /// 查询当前是否已启用开机自启（检查快捷方式是否存在）。
    /// </summary>
    /// <returns>已启用返回 true</returns>
    public static bool IsEnabled()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            return File.Exists(shortcutPath);
        }
        catch (Exception ex) { Logger.Error("检查开机自启状态失败", ex); return false; }
    }

    /// <summary>
    /// 创建 .lnk 快捷方式（通过 WScript.Shell COM 对象）。
    /// </summary>
    /// <param name="shortcutPath">快捷方式保存路径</param>
    /// <param name="targetPath">目标 exe 路径</param>
    /// <param name="arguments">启动参数</param>
    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        // 通过 COM 调用 WScript.Shell 创建快捷方式
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;       // 目标程序路径
        shortcut.Arguments = arguments;          // 启动参数
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);  // 工作目录
        shortcut.WindowStyle = 1;                // 1 = 正常窗口
        shortcut.Save();
    }
}
