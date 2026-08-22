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

// ============================================================================
// MainWindow.Tray.cs — 主窗口的"系统托盘"部分类（partial class 拆分文件之一）
// 职责：
//   1) 初始化系统托盘图标，绑定双击/右键菜单/开关追踪/退出等回调；
//   2) 通过 Win32 窗口钩子(WndProc)接收托盘消息并转发给 TrayIcon 处理；
//   3) 拦截窗口关闭事件：点 X 默认隐藏到托盘而非退出（可在设置中关闭）；
//   4) 真正退出时统一停止后台服务并释放托盘资源。
// 协作对象：Services.TrayIcon(托盘封装)、SettingsRepository(读取配置)、
//           TrackingEngine / ScreenshotService / SummaryScheduler(后台服务)。
// ============================================================================
public partial class MainWindow
{
    /// <summary>
    /// 初始化系统托盘：挂钩窗口消息、创建托盘图标并绑定各交互回调。
    /// 由主窗口初始化流程调用一次。
    /// </summary>
    private void InitTray()
    {
        // 获取本 WPF 窗口的 Win32 句柄所对应的消息源(HwndSource)，
        // 托盘图标的回调消息不走 WPF 路由事件，必须用 Win32 消息钩子接收。
        var hwndSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        // 注册消息钩子：发到该窗口句柄的 Win32 消息都会先流经 WndProc
        hwndSource?.AddHook(WndProc);

        // 创建托盘图标：传入宿主窗口句柄（Shell 通知消息的回传目标）与悬浮提示文本
        _trayIcon = new TrayIcon(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            "TimeActivity");
        // 双击托盘图标 → 从托盘恢复并激活主窗口
        _trayIcon.OnDoubleClick = () => ShowFromTray();
        _trayIcon.OnShowMenu = () =>
        {
            _trayIcon.ShowContextMenuAtCursor(_engine.IsRunning);
        };
        // 托盘菜单"开始/停止追踪" → 复用主界面按钮的事件处理，
        // 保证托盘操作与主界面操作走同一条状态切换路径，不会出现两套状态。
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

    /// <summary>
    /// Win32 窗口消息钩子：过滤出托盘自定义消息并交给 TrayIcon 解析分发。
    /// </summary>
    /// <param name="hwnd">接收消息的窗口句柄</param>
    /// <param name="msg">消息 ID</param>
    /// <param name="wParam">附加参数（托盘消息中含鼠标事件类型）</param>
    /// <param name="lParam">附加参数</param>
    /// <param name="handled">是否已处理该消息（阻止继续默认派发）</param>
    /// <returns>固定返回 0，表示无额外返回值</returns>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 只关心托盘图标回传的自定义回调消息（WM_TRAYICON 为 TrayIcon 注册的消息 ID）
        if (msg == TrayIcon.WM_TRAYICON)
        {
            // 把鼠标事件的 wParam/lParam 转交 TrayIcon 内部解析成双击/右键等语义事件
            _trayIcon?.HandleMessage(wParam, lParam);
            handled = true; // 标记已处理，WPF 不再做默认处理
        }
        return IntPtr.Zero;
    }

    /// <summary>从托盘恢复主窗口：显示、还原尺寸并抢焦点置前。</summary>
    private void ShowFromTray()
    {
        Show();                          // 撤销 Hide() 的隐藏状态
        WindowState = WindowState.Normal;// 若之前是最小化则还原为普通大小
        Activate();                      // 激活窗口并将其带到前台
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

        // —— 真正退出的清理流程：按依赖顺序停掉全部后台服务 ——
        _engine.Stop();           // 停止活动追踪轮询（会落库最后一条未完结活动）
        _screenshotService.Stop();// 停止定时截图服务
        _summaryScheduler.Stop(); // 停止日/周/月 AI 总结调度器
        _trayIcon?.Dispose();     // 释放托盘图标（否则退出后残留幽灵图标）
        base.OnClosing(e);        // 继续默认关闭流程（释放窗口资源）
    }

}
