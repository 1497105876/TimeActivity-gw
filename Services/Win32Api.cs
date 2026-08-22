// ============================================================================
// Win32Api.cs — Win32 API 的 P/Invoke 封装（静态类）
// 职责：取前台窗口句柄、由句柄取进程名/窗口标题、取最后一次输入的空闲秒数。
// 全部为无状态静态调用，供 TrackingEngine 每轮采样使用。
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace TimeActivity.Services;

/// <summary>
/// Win32 API 封装 — 用来抓当前前台窗口的信息
/// </summary>
public static class Win32Api
{
    // Win32 API：获取当前前台窗口句柄（用户正在操作的窗口）
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // 获取窗口标题文字
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    // 通过窗口句柄获取对应的进程 ID
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Win32 结构体：记录最后一次输入操作的时间
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;  // 结构体大小
        public uint dwTime;  // 最后一次输入的时间戳（GetTickCount 值）
    }

    // 获取系统最后一次输入信息（用来计算用户空闲了多久）
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // 获取系统启动以来的毫秒数（64 位不会溢出）
    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    // 获取系统启动以来的毫秒数（32 位，约49.7天回绕，但 uint 减法自动处理回绕）
    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    /// <summary>
    /// 获取当前前台窗口的标题文字。
    /// </summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <returns>窗口标题字符串</returns>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hWnd, sb, 512);
        return sb.ToString();
    }

    /// <summary>
    /// 获取当前前台窗口对应的进程名（不含 .exe 后缀）。
    /// </summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <returns>进程名，获取失败返回 "unknown"</returns>
    public static string GetProcessName(IntPtr hWnd)
    {
        // 通过窗口句柄拿进程 ID
        GetWindowThreadProcessId(hWnd, out uint pid);
        try
        {
            // Process 对象用完必须释放，否则长时间运行会耗尽 OS 句柄
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 获取用户空闲了多久（秒）。原理：GetTickCount - 最后一次输入时间。
    /// uint 减法自动处理 49.7 天回绕（uint 溢出后减法结果仍然正确）。
    /// </summary>
    /// <returns>空闲秒数</returns>
    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        // 调用失败（如会话锁定/权限异常）时 info.dwTime 不会被填充，dwTime 保持 0，
        // 会让 elapsed 变成"开机以来的全部时间"，误判为永久空闲、追踪静默失效。
        // 失败按空闲 0 秒处理并记录一条 WARN，便于排查。
        if (!GetLastInputInfo(ref info))
        {
            Logger.Warning("获取最后输入信息失败（GetLastInputInfo 返回 false），按空闲 0 秒处理");
            return 0;
        }
        // 用 GetTickCount（uint）与 dwTime（uint）做差，uint 减法自动处理回绕
        // 例：dwTime=0xFFFFFFFF（回绕前最后一刻），now=0x00000001（回绕后）
        // uint 减法：0x00000001 - 0xFFFFFFFF = 0x00000002（即 2ms，正确）
        uint now = GetTickCount();
        uint elapsed = now - info.dwTime;
        return (int)(elapsed / 1000);
    }
}
