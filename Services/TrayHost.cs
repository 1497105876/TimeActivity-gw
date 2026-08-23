// ============================================================================
// TrayHost.cs — 托盘宿主窗口（2026-08-23 方案A 新增）
// 作用：开机自启 --minimized 时，应用不再创建 MainWindow，仅由本宿主承担：
//   1) 提供接收托盘消息的 Win32 句柄（0x0 不可见窗口，定位到屏幕外）；
//   2) 持有 TrayIcon 并处理菜单（显示主界面/开关追踪/退出）；
//   3) 首次"显示主界面"时才真正 new MainWindow()（延迟创建：更快、更省内存，
//      同时彻底规避登录早期 GPU 未就绪导致的黑框）。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Interop;
using TimeActivity.Data;

namespace TimeActivity.Services;

public class TrayHost : Window
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;

    public TrayHost()
    {
        Title = "TimeActivity 托盘宿主";
        Width = 0; Height = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        // 窗口定位到屏幕外，用户永远看不到这个 0x0 宿主
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        Loaded += (_, _) => InitTray();
    }

    /// <summary>创建/获取主窗口（延迟创建的唯一入口）。窗口关闭时解挂引擎事件，允许下次重建。</summary>
    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow == null)
        {
            Logger.Info("首次从托盘打开：创建主窗口（延迟创建）");
            _mainWindow = new MainWindow();
            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow.DetachFromServices(); // 解除引擎事件/设置事件订阅并停窗口定时器
                _mainWindow = null;
            };
        }
        return _mainWindow;
    }

    private void InitTray()
    {
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        _trayIcon = new TrayIcon(new WindowInteropHelper(this).Handle, "TimeActivity");
        UpdateTooltip();
        _trayIcon.OnDoubleClick = () => ShowMain();
        _trayIcon.OnShowMenu = () => _trayIcon.ShowContextMenuAtCursor(AppServices.Engine.IsRunning);
        _trayIcon.OnToggleTracking = () =>
        {
            if (AppServices.Engine.IsRunning) AppServices.StopTracking();
            else AppServices.StartTracking();
            UpdateTooltip();
            if (_mainWindow != null) _mainWindow.RefreshTrackingButtons();
        };
        _trayIcon.OnExit = () =>
        {
            if (_mainWindow != null) _mainWindow.ForceClose();
            Application.Current.Shutdown();
        };
    }

    /// <summary>从托盘显示主窗口。</summary>
    private void ShowMain()
    {
        var win = EnsureMainWindow();
        win.ShowFromTray();
        UpdateTooltip();
    }

    /// <summary>启动流程用：非 --minimized 时立即创建并正常显示主窗口。</summary>
    public void ShowMainFromStartup()
    {
        var win = EnsureMainWindow();
        win.Show();
    }

    /// <summary>托盘悬浮提示随状态更新。</summary>
    public void UpdateTooltip()
    {
        _trayIcon?.UpdateTooltip($"TimeActivity — {(AppServices.Engine.IsRunning ? "追踪中" : "已停止")}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayIcon.WM_TRAYICON)
        {
            _trayIcon?.HandleMessage(wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }
}
