// ============================================================================
// AutoStartHelper.cs — 开机自启管理（静态类）
// 职责：通过"启动文件夹快捷方式(.lnk)"实现开机自启的开启/关闭与状态查询；
//       相比写注册表更直观、用户可在启动文件夹中自行删除。
// ============================================================================
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
    // 固定文件名：重复 Enable 会覆盖旧快捷方式（WScript Save 覆盖语义）
    private const string ShortcutName = "TimeActivity.lnk";

    /// <summary>
    /// 启用开机自启：在启动文件夹创建快捷方式，带 --minimized 参数启动后最小化到托盘。
    /// </summary>
    public static void Enable()
    {
        // 全流程包 try：自启失败只记日志，绝不能阻断设置界面操作
        try
        {
            // 当前用户的启动文件夹（%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup）
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            // 获取当前 exe 的完整路径
            // MainModule 对普通桌面程序可靠；null 时用 ! 断言，若真为 null 会抛 NRE 被 catch 捕获
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
            // 先判存在再删；不存在时静默跳过（幂等）
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
        // GetTypeFromProgID 返回 COM 类型的 Runtime 类型；! 断言非 null
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        // Activator.CreateInstance 实例化 COM 对象（RCW 包装），dynamic 走后期绑定调用
        dynamic shell = Activator.CreateInstance(shellType)!;
        // CreateShortcut 只是在内存里建对象，必须最后 Save() 才真正落盘 .lnk 文件
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;       // 目标程序路径
        shortcut.Arguments = arguments;          // 启动参数
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);  // 工作目录
        shortcut.WindowStyle = 1;                // 1 = 正常窗口
        // 落盘；此后 shell/shortcut 两个 RCW 交给 GC 终结器释放 COM 引用
        shortcut.Save();
    }
}
