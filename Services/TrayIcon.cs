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
    // Win32 API：操作系统托盘图标（添加/修改/删除）
    // dwMessage 指定操作类型（NIM_ADD/NIM_MODIFY/NIM_DELETE），pnid 是托盘图标数据
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW pnid);

    // 加载系统预定义图标（如 IDI_APPLICATION）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    // 创建弹出菜单（右键菜单）
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    // 往菜单追加菜单项（文字项或分隔符）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    // 在指定位置显示弹出菜单，返回用户选择的菜单项 ID
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    // 销毁菜单，用完必须调
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    // 从文件或资源加载图片（图标/光标/位图），这里用来加载系统图标资源
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    // 托盘操作消息类型
    private const int NIM_ADD = 0x00000000;    // 添加图标到托盘
    private const int NIM_MODIFY = 0x00000001; // 修改已有托盘图标
    private const int NIM_DELETE = 0x00000002; // 从托盘删除图标

    // 托盘数据标志位：通知消息+图标+提示文字
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    // 菜单项类型标志
    private const uint MF_STRING = 0x00000000;     // 文字菜单项
    private const uint MF_SEPARATOR = 0x00000800;  // 分隔线

    // 弹出菜单的显示标志
    private const uint TPM_RIGHTBUTTON = 0x0002;   // 右键也能选择菜单项
    private const uint TPM_LEFTALIGN = 0x0000;     // 菜单左对齐
    private const uint TPM_BOTTOMALIGN = 0x0020;   // 菜单底部对齐（向上弹出）
    private const uint TPM_RETURNCMD = 0x0100;     // 让 TrackPopupMenu 返回选中的菜单项 ID

    // 自定义窗口消息基址，托盘回调消息用 WM_APP+1
    private const int WM_APP = 0x8000;
    public const int WM_TRAYICON = WM_APP + 1;     // 托盘图标回调消息
    private const int WM_LBUTTONDBLCLK = 0x0203;   // 左键双击
    private const int WM_RBUTTONUP = 0x0205;        // 右键抬起

    // 托盘图标数据结构（Win32 NOTIFYICONDATAW）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;       // 结构体大小
        public IntPtr hWnd;      // 接收回调消息的窗口句柄
        public uint uID;         // 托盘图标 ID（同一窗口可以有多个托盘图标）
        public uint uFlags;      // 指定哪些字段有效（NIF_MESSAGE|NIF_ICON|NIF_TIP）
        public uint uCallbackMessage; // 回调消息号（托盘交互时发给 hWnd）
        public IntPtr hIcon;     // 图标句柄
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;     // 鼠标悬停提示文字
    }

    // 宿主窗口句柄
    private IntPtr _hWnd;
    // 图标句柄
    private IntPtr _hIcon;
    // 图标是否已添加到托盘
    private bool _added;
    // 托盘图标 ID
    private readonly uint _uID = 1;

    // 用户交互回调（双击托盘图标、右键菜单触发时调用）
    public Action? OnDoubleClick;
    public Action? OnShowMenu;

    // Win32 API：获取鼠标当前位置
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // 释放图标句柄，防止 GDI 泄漏
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>
    /// 构造函数：创建托盘图标，加载默认图标，注册到系统托盘。
    /// </summary>
    /// <param name="hWnd">接收托盘消息的窗口句柄</param>
    /// <param name="tooltip">鼠标悬停时显示的提示文字</param>
    public TrayIcon(IntPtr hWnd, string tooltip = "TimeActivity")
    {
        _hWnd = hWnd;
        _hIcon = LoadDefaultIcon();

        // 构建托盘数据并添加到系统托盘
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hWnd,
            uID = _uID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,  // 回调消息+图标+提示都有效
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
    /// <summary>
    /// 显示右键菜单（显示主窗口/开始或停止追踪/退出）。
    /// </summary>
    /// <param name="x">菜单左上角 X 坐标</param>
    /// <param name="y">菜单左上角 Y 坐标</param>
    /// <param name="isRunning">当前是否正在追踪，决定菜单文字显示"开始"还是"停止"</param>
    public void ShowContextMenu(int x, int y, bool isRunning)
    {
        // 创建弹出菜单
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        // 追加菜单项：显示主窗口、开始/停止追踪、分隔线、退出
        AppendMenuW(hMenu, MF_STRING, 1, "显示主窗口");
        AppendMenuW(hMenu, MF_STRING, 2, isRunning ? "停止追踪" : "开始追踪");
        AppendMenuW(hMenu, MF_SEPARATOR, 0, "");
        AppendMenuW(hMenu, MF_STRING, 3, "退出");

        // 显示菜单并等待用户选择，返回值是菜单项 ID
        int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD, x, y, 0, _hWnd, IntPtr.Zero);
        DestroyMenu(hMenu);  // 用完销毁

        // 根据用户选择触发对应回调
        switch (cmd)
        {
            case 1: OnDoubleClick?.Invoke(); break;   // 显示主窗口
            case 2: OnToggleTracking?.Invoke(); break; // 开始/停止追踪
            case 3: OnExit?.Invoke(); break;           // 退出
        }
    }

    // 切换追踪状态的回调
    public Action? OnToggleTracking;
    // 退出程序的回调
    public Action? OnExit;

    /// <summary>
    /// 更新托盘图标的鼠标悬停提示文字。
    /// </summary>
    /// <param name="text">新的提示文字</param>
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

    /// <summary>
    /// 加载系统默认应用图标（先试 LoadImageW，失败了用 LoadIconW 兜底）。
    /// </summary>
    /// <returns>图标句柄</returns>
    private static IntPtr LoadDefaultIcon()
    {
        // "#32512" 是 IDI_APPLICATION 的资源编号，系统默认应用图标
        IntPtr hIcon = LoadImageW(IntPtr.Zero, "#32512", 1, 0, 0, 0x00000010 | 0x00008000);
        if (hIcon == IntPtr.Zero)
            hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION 兜底
        return hIcon;
    }

    /// <summary>
    /// 释放资源：从系统托盘移除图标。
    /// </summary>
    public void Dispose()
    {
        if (_added)
        {
            // 从托盘删除图标
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = _uID
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        // 释放图标句柄
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
