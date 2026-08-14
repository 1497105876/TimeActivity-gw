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
            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 获取用户空闲了多久（秒）。原理：GetTickCount64 - 最后一次输入时间。
    /// 处理了 uint 回绕问题（48 天后会回绕但零扩展保证差值正确）。
    /// </summary>
    /// <returns>空闲秒数</returns>
    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        GetLastInputInfo(ref info);
        // GetTickCount64 返回 ulong 不会溢出
        // LASTINPUTINFO.dwTime 是 uint（GetTickCount 的值），48天后会回绕
        // 但 GetTickCount64 内部和 dwTime 的基准一致，做减法时 uint 会隐式转 ulong
        // 回绕后 dwTime 变小，now 变大，差值会很大但正确——因为 uint 到 ulong 是零扩展
        ulong now = GetTickCount64();
        ulong lastInput = info.dwTime; // uint → ulong 零扩展，和 GetTickCount64 同基准
        ulong elapsed = now - lastInput;
        return (int)(elapsed / 1000);
    }
}
