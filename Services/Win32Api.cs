using System;
using System.Runtime.InteropServices;

namespace TimeActivity.Services;

/// <summary>
/// Win32 API 封装 — 用来抓当前前台窗口的信息
/// </summary>
public static class Win32Api
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    /// <summary>
    /// 获取当前前台窗口的标题
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hWnd, sb, 512);
        return sb.ToString();
    }

    /// <summary>
    /// 获取当前前台窗口对应的进程名
    /// </summary>
    public static string GetProcessName(IntPtr hWnd)
    {
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
    /// 获取用户空闲了多久（秒）
    /// </summary>
    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        GetLastInputInfo(ref info);
        uint now = GetTickCount();
        // 处理 GetTickCount 溢出回绕
        uint elapsed = now - info.dwTime;
        return (int)(elapsed / 1000);
    }
}
