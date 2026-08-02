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
    public static extern ulong GetTickCount64();

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
