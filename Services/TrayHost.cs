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
    /// <summary>托盘图标封装（纯 Win32）；窗口 Loaded、句柄就绪后才创建，其资源释放由 App 退出路径统一负责。</summary>
    private TrayIcon? _trayIcon;
    // 主窗口懒引用：null 表示尚未创建或已被用户关闭（可重建）
    /// <summary>主窗口懒引用：null = 从未创建或已被用户关闭（可重建）；首次需要显示时才真正 new MainWindow()。</summary>
    private MainWindow? _mainWindow;

    // Win32：扩展样式操作（让宿主窗口从 Alt+Tab / 任务视图中彻底消失）
    /// <summary>读取窗口扩展样式（user32.dll，nIndex=GWL_EXSTYLE）。失败返回 0。</summary>
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    /// <summary>写回窗口扩展样式（user32.dll）：把隐身所需的两个位"或"进去再写回。</summary>
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    /// <summary>GetWindowLong/SetWindowLong 的索引常量：表示操作的是扩展样式（GWL_EXSTYLE）。</summary>
    private const int GWL_EXSTYLE = -20;
    /// <summary>扩展样式 WS_EX_TOOLWINDOW：工具窗口，不出现在 Alt+Tab 任务切换列表。</summary>
    private const int WS_EX_TOOLWINDOW = 0x80;       // 工具窗口：不出现在 Alt+Tab
    /// <summary>扩展样式 WS_EX_NOACTIVATE：激活时不抢占焦点，避免干扰用户正在进行的输入。</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000; // 不抢焦点

    /// <summary>
    /// 句柄创建后追加 WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE 扩展样式：
    /// 从 Alt+Tab、Win+Tab 任务视图列表中排除该宿主窗口（它只负责托盘交互）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        // 先执行基类逻辑保证 HwndSource/句柄已就绪，再取句柄做样式修改
        base.OnSourceInitialized(e);
        // 取出本窗口的 HWND（窗口句柄此后生命周期内稳定不变）
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // 读出当前扩展样式（保留已有位，只在下面"或"进两个隐身位）
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        // 写回：加上"工具窗口"与"不抢焦点"，从任务视图与焦点争夺中消失
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// 构造：把自身配置成 0x0、无边框、不进任务栏、屏幕外的隐形窗口。
    /// </summary>
    public TrayHost()
    {
        Title = "TimeActivity 托盘宿主";
        // 尺寸归零：0x0 的窗口没有可视区域，纯作句柄载体
        Width = 0; Height = 0;
        // 无边框、禁止用户调整大小：杜绝任何可见 UI 的呈现可能
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // 不进任务栏、激活时不抢焦点：保证用户完全感知不到它的存在
        ShowInTaskbar = false;
        ShowActivated = false;
        // 窗口定位到屏幕外，用户永远看不到这个 0x0 宿主
        // -32000 在多显示器/超大虚拟桌面坐标下也不会落在可见区域
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        // WPF 窗口必须有句柄后才能挂托盘（TrayIcon 需要 HWND），故推迟到 Loaded
        // Loaded 事件在窗口首次显示、句柄创建完成后触发，此时 InitTray 才能拿到 HWND
        Loaded += (_, _) => InitTray();
    }

    /// <summary>创建/获取主窗口（延迟创建的唯一入口）。窗口关闭时解挂引擎事件并回收内存，允许下次重建。</summary>
    private MainWindow EnsureMainWindow()
    {
        // 已存在（创建过且未被关闭）则直接复用，避免重复 new
        if (_mainWindow == null)
        {
            // 首次需要显示时才创建：延迟到此刻，启动更快、内存占用更小
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
        // 返回主窗口给调用方显示
        return _mainWindow;
    }

    /// <summary>
    /// Loaded 回调：窗口句柄就绪后挂托盘图标与消息钩子，并接线全部菜单回调。
    /// </summary>
    private void InitTray()
    {
        // 取本窗口的 HwndSource 以注册 WndProc 钩子（接收 WM_TRAYICON）
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        // 把 WndProc 挂进窗口消息链：此后托盘回调消息（WM_TRAYICON）会先经这里
        hwndSource?.AddHook(WndProc);

        // 创建托盘图标：回调消息发往本窗口句柄
        _trayIcon = new TrayIcon(new WindowInteropHelper(this).Handle, "TimeActivity");
        // 先按当前追踪状态刷一遍提示文字（此时引擎尚未启动则显示"已停止"）
        UpdateTooltip();
        // 双击图标 = 显示主窗口（触发延迟创建）
        _trayIcon.OnDoubleClick = () => ShowMain();
        // 右键抬起 = 在光标处弹菜单，菜单文案随追踪状态变化
        _trayIcon.OnShowMenu = () => _trayIcon.ShowContextMenuAtCursor(AppServices.Engine.IsRunning);
        // 菜单"开始/停止追踪"：切换后同步托盘提示与主窗口按钮状态（若主窗口存在）
        _trayIcon.OnToggleTracking = () =>
        {
            // 按当前运行状态反向切换：在跑就停，没跑就起
            if (AppServices.Engine.IsRunning) AppServices.StopTracking();
            else AppServices.StartTracking();
            // 同步托盘悬浮提示文字（"追踪中/已停止"）
            UpdateTooltip();
            // 主窗口若已创建，同步刷新其追踪开关按钮的 UI 状态
            if (_mainWindow != null) _mainWindow.RefreshTrackingButtons();
        };
        // 菜单"退出"：若有主窗口先强制关（走其清理逻辑），再结束整个应用
        _trayIcon.OnExit = () =>
        {
            // ForceClose 负责主窗口的清理与事件解绑，避免退出路径遗漏
            if (_mainWindow != null) _mainWindow.ForceClose();
            // 结束整个 WPF 应用（App 退出路径统一释放托盘等资源）
            Application.Current.Shutdown();
        };
    }

    /// <summary>从托盘显示主窗口。</summary>
    private void ShowMain()
    {
        // 取（必要时创建）主窗口后，以"从托盘恢复"的方式显示并置前
        var win = EnsureMainWindow();
        win.ShowFromTray();
        // 显示后主窗口可见，托盘提示里的"追踪状态"文案保持一致
        UpdateTooltip();
    }

    /// <summary>启动流程用：非 --minimized 时立即创建并正常显示主窗口。</summary>
    public void ShowMainFromStartup()
    {
        // 正常启动路径：创建主窗口并普通显示（不同于 ShowMain 的托盘恢复语义）
        var win = EnsureMainWindow();
        win.Show();
    }

    /// <summary>托盘悬浮提示随状态更新。</summary>
    public void UpdateTooltip()
    {
        // 提示文字实时反映追踪开关状态；图标未创建（_trayIcon 为空）时安全跳过
        _trayIcon?.UpdateTooltip($"TimeActivity — {(AppServices.Engine.IsRunning ? "追踪中" : "已停止")}");
    }

    /// <summary>
    /// Win32 消息钩子：只认 WM_TRAYICON，转交 TrayIcon 解析鼠标语义；
    /// 其余消息一律放行给 WPF 默认处理。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 只拦截托盘回调消息（WM_APP+1），其余交给默认 WndProc 流程
        if (msg == TrayIcon.WM_TRAYICON)
        {
            // 交给 TrayIcon 按 wParam(图标ID)/lParam(鼠标消息) 分发到对应 Action
            _trayIcon?.HandleMessage(wParam, lParam);
            // 置 handled=true：告知 WPF 该消息已被处理，不必再走默认逻辑
            handled = true;
        }
        // 本钩子不消费返回结果，统一返回 0
        return IntPtr.Zero;
    }
}
