// ============================================================================
// AutoStartHelper.cs — 开机自启管理（静态类）
// 职责：通过"启动文件夹快捷方式(.lnk)"实现开机自启的开启/关闭与状态查询；
//       相比写注册表更直观、用户可在启动文件夹中自行删除。
// ============================================================================
using System;                  // Environment、Type、Activator、Exception
using System.IO;               // Path、File

namespace TimeActivity.Services;

/// <summary>
/// 开机自启管理 — 通过启动文件夹创建/删除 .lnk 快捷方式
/// 遵循 SRP：只管开机自启的启用/禁用
/// </summary>
/// <remarks>
/// 没有写 Run 注册表项，而是往"当前用户的启动文件夹"放快捷方式：
/// 不需要管理员权限，用户也能自己在资源管理器里删掉。
/// 注意：快捷方式带 --minimized 参数，App.OnStartup 据此跳过主窗口创建，只起托盘宿主。
/// </remarks>
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
            // 只对当前用户生效，不写 HKEY_LOCAL_MACHINE，因此不需要管理员权限
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            // 拼出完整 .lnk 路径
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            // 获取当前 exe 的完整路径
            // MainModule 对普通桌面程序可靠；null 时用 ! 断言，若真为 null 会抛 NRE 被 catch 捕获
            // 单文件发布/自解压场景下 MainModule 可能拿不到，届时自启会静默失败并记日志
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            // 建快捷方式；--minimized 让用户开机后不弹主窗口，直接在托盘后台记录
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
            // 与 Enable 相同的路径推导逻辑
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);
            // 先判存在再删；不存在时静默跳过（幂等）
            // 用户手工删掉快捷方式后，再点"关闭自启"也不会报错
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
            // 以"快捷方式文件是否存在"作为唯一判据：
            // 文件存在但指向已被卸载的 exe 时仍返回 true（属于已知边界）
            return File.Exists(shortcutPath);
        }
        // 取不到启动文件夹路径（极罕见）时按"未启用"处理，避免设置页勾选框崩掉
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
        // 依赖系统自带的 Windows Script Host 组件；组策略禁用 WSH 的机器上会在此抛异常
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        // Activator.CreateInstance 实例化 COM 对象（RCW 包装），dynamic 走后期绑定调用
        dynamic shell = Activator.CreateInstance(shellType)!;
        // CreateShortcut 只是在内存里建对象，必须最后 Save() 才真正落盘 .lnk 文件
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;       // 目标程序路径
        shortcut.Arguments = arguments;          // 启动参数
        // 工作目录设为 exe 所在目录：不设的话相对路径读写（数据库、总结文件）会跑到系统目录
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);  // 工作目录
        shortcut.WindowStyle = 1;                // 1 = 正常窗口
        // 落盘；此后 shell/shortcut 两个 RCW 交给 GC 终结器释放 COM 引用
        shortcut.Save();
    }
}
