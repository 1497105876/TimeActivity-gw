using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TimeActivity.Data;
using TimeActivity.Helpers;
using TimeActivity.Models;
using TimeActivity.Rendering;
using TimeActivity.Services;

namespace TimeActivity;

public partial class MainWindow
{
    private void InitTray()
    {
        var hwndSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        _trayIcon = new TrayIcon(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            "TimeActivity");
        _trayIcon.OnDoubleClick = () => ShowFromTray();
        _trayIcon.OnShowMenu = () =>
        {
            _trayIcon.ShowContextMenuAtCursor(_engine.IsRunning);
        };
        _trayIcon.OnToggleTracking = () =>
        {
            if (_engine.IsRunning) BtnStop_Click(this, new RoutedEventArgs());
            else BtnStart_Click(this, new RoutedEventArgs());
        };
        _trayIcon.OnExit = () =>
        {
            _forceClose = true;
            Close();
        };
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

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭按钮 → 最小化到托盘（除非是强制退出）
        if (!_forceClose && SettingsRepository.Get("MinimizeToTray", "true") == "true")
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.UpdateTooltip($"TimeActivity — {(_engine.IsRunning ? "追踪中" : "已停止")}");
            return;
        }

        // 真正退出
        _engine.Stop();
        _screenshotService.Stop();
        _summaryScheduler.Stop();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

}
