// ============================================================================
// TrayHost.cs — 托盘宿主窗口（2026-08-23 方案A 新增）
// 作用：开机自启 --minimized 时，应用不再创建 MainWindow，仅由本宿主承担：
//   1) 提供接收托盘消息的 Win32 句柄（0x0 不可见窗口，定位到屏幕外）；
//   2) 持有 TrayIcon 并处理菜单（显示主界面/开关追踪/退出）；
//   3) 首次"显示主界面"时才真正 new MainWindow()（延迟创建：更快、更省内存，
//      同时彻底规避登录早期 GPU 未就绪导致的黑框）。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// 托盘宿主窗口：最小化的常驻壳，承载托盘图标并按需延迟创建主窗口
/// </summary>
public class TrayHost : Window
{
    // 托盘图标（Loaded 后创建；Dispose 由 App 退出路径负责）
    private TrayIcon? _trayIcon;
    // 主窗口懒引用：null 表示尚未创建或已被用户关闭（可重建）
    private MainWindow? _mainWindow;

    // Win32：扩展样式操作（让宿主窗口从 Alt+Tab / 任务视图中彻底消失）
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;       // 工具窗口：不出现在 Alt+Tab
    private const int WS_EX_NOACTIVATE = 0x08000000; // 不抢焦点

    /// <summary>
    /// 句柄创建后追加 WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE 扩展样式：
    /// 从 Alt+Tab、Win+Tab 任务视图列表中排除该宿主窗口（它只负责托盘交互）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// 构造：把自身配置成 0x0、无边框、不进任务栏、屏幕外的隐形窗口。
    /// </summary>
    public TrayHost()
    {
        Title = "TimeActivity 托盘宿主";
        Width = 0; Height = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        // 窗口定位到屏幕外，用户永远看不到这个 0x0 宿主
        // -32000 在多显示器/超大虚拟桌面坐标下也不会落在可见区域
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        // WPF 窗口必须有句柄后才能挂托盘（TrayIcon 需要 HWND），故推迟到 Loaded
        Loaded += (_, _) => InitTray();
    }

    /// <summary>创建/获取主窗口（延迟创建的唯一入口）。窗口关闭时解挂引擎事件并回收内存，允许下次重建。</summary>
    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow == null)
        {
            Logger.Info("首次从托盘打开：创建主窗口（延迟创建）");
            _mainWindow = new MainWindow();
            // 用户关掉主窗口时不退出程序（仍驻留托盘），只解除引用等待下次重建
            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow.DetachFromServices(); // 解除引擎事件/设置事件订阅并停窗口定时器
                _mainWindow = null;               // 置空引用：窗口对象连同可视树交给 GC
                // 2026-08-25 方案B：窗口已真正销毁，此刻触发一次工作集释放 + 压缩回收，
                // 让"点 X 后后台内存立刻回落"（事件驱动，非周期调用）
                AppServices.OnMinimizedToTray();
            };
        }
        return _mainWindow;
    }

    /// <summary>
    /// Loaded 回调：窗口句柄就绪后挂托盘图标与消息钩子，并接线全部菜单回调。
    /// </summary>
    private void InitTray()
    {
        // 取本窗口的 HwndSource 以注册 WndProc 钩子（接收 WM_TRAYICON）
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        // 创建托盘图标：回调消息发往本窗口句柄
        _trayIcon = new TrayIcon(new WindowInteropHelper(this).Handle, "TimeActivity");
        UpdateTooltip();
        // 双击图标 = 显示主窗口（触发延迟创建）
        _trayIcon.OnDoubleClick = () => ShowMain();
        // 右键抬起 = 在光标处弹菜单，菜单文案随追踪状态变化
        _trayIcon.OnShowMenu = () => _trayIcon.ShowContextMenuAtCursor(AppServices.Engine.IsRunning);
        // 菜单"开始/停止追踪"：切换后同步托盘提示与主窗口按钮状态（若主窗口存在）
        _trayIcon.OnToggleTracking = () =>
        {
            if (AppServices.Engine.IsRunning) AppServices.StopTracking();
            else AppServices.StartTracking();
            UpdateTooltip();
            if (_mainWindow != null) _mainWindow.RefreshTrackingButtons();
        };
        // 菜单"退出"：若有主窗口先强制关（走其清理逻辑），再结束整个应用
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

    /// <summary>
    /// Win32 消息钩子：只认 WM_TRAYICON，转交 TrayIcon 解析鼠标语义；
    /// 其余消息一律放行给 WPF 默认处理。
    /// </summary>
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
