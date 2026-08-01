using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;

namespace TimeActivity.Services;

/// <summary>
/// 系统托盘图标（纯 Win32 API，不引 WinForms）
/// </summary>
public class TrayIcon : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW pnid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int WM_APP = 0x8000;
    public const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    private IntPtr _hWnd;
    private IntPtr _hIcon;
    private bool _added;
    private readonly uint _uID = 1;

    // 通知回调
    public Action? OnDoubleClick;
    public Action? OnShowMenu;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public TrayIcon(IntPtr hWnd, string tooltip = "TimeActivity")
    {
        _hWnd = hWnd;
        _hIcon = LoadDefaultIcon();

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hWnd,
            uID = _uID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip
        };
        Shell_NotifyIconW(NIM_ADD, ref data);
        _added = true;
    }

    /// <summary>
    /// 处理托盘消息，返回 true 表示已处理
    /// </summary>
    public bool HandleMessage(IntPtr wParam, IntPtr lParam)
    {
        if ((int)wParam != _uID) return false;

        int msg = (int)lParam & 0xFFFF;
        switch (msg)
        {
            case WM_LBUTTONDBLCLK:
                OnDoubleClick?.Invoke();
                return true;
            case WM_RBUTTONUP:
                OnShowMenu?.Invoke();
                return true;
        }
        return false;
    }

    /// <summary>
    /// 在鼠标当前位置显示右键菜单
    /// </summary>
    public void ShowContextMenuAtCursor(bool isRunning)
    {
        GetCursorPos(out POINT pt);
        ShowContextMenu(pt.X, pt.Y, isRunning);
    }

    /// <summary>
    /// 显示右键菜单
    /// </summary>
    public void ShowContextMenu(int x, int y, bool isRunning)
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        AppendMenuW(hMenu, MF_STRING, 1, "显示主窗口");
        AppendMenuW(hMenu, MF_STRING, 2, isRunning ? "停止追踪" : "开始追踪");
        AppendMenuW(hMenu, MF_SEPARATOR, 0, "");
        AppendMenuW(hMenu, MF_STRING, 3, "退出");

        int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD, x, y, 0, _hWnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case 1: OnDoubleClick?.Invoke(); break;
            case 2: OnToggleTracking?.Invoke(); break;
            case 3: OnExit?.Invoke(); break;
        }
    }

    public Action? OnToggleTracking;
    public Action? OnExit;

    public void UpdateTooltip(string text)
    {
        if (!_added) return;
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = _uID,
            uFlags = NIF_TIP,
            szTip = text
        };
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private static IntPtr LoadDefaultIcon()
    {
        // 用系统默认应用图标
        IntPtr hIcon = LoadImageW(IntPtr.Zero, "#32512", 1, 0, 0, 0x00000010 | 0x00008000);
        if (hIcon == IntPtr.Zero)
            hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
        return hIcon;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = _uID
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
    }
}
