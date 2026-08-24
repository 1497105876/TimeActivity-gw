// ============================================================================
// App.xaml.cs — 应用入口类（2026-08-23 方案A 重写启动流程）
// 流程：
//   1) AppServices.Initialize()：建库/按需重分类/创建并按配置启动后台服务；
//   2) 创建托盘宿主 TrayHost（0x0 屏幕外窗口，承担托盘图标与消息）；
//   3) 仅当非 --minimized 启动时才立即创建并显示 MainWindow；
//      --minimized（开机自启）时只驻留托盘，首次点击托盘再延迟创建主窗口。
//   退出时 OnExit 统一停止全部后台服务。
// ============================================================================
using System;
using System.Windows;
using TimeActivity.Services;

namespace TimeActivity;

public partial class App : Application
{
    /// <summary>托盘宿主引用（供主窗口刷新托盘提示等）。</summary>
    public TrayHost? Host { get; private set; }

    /// <summary>
    /// 启动入口重写：初始化后台服务 → 创建托盘宿主 → 按命令行参数决定是否立即显示主窗口。
    /// </summary>
    /// <param name="e">启动参数（可含 --minimized 表示开机自启静默驻留）</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 先执行 WPF 默认启动流程（触发 Startup 事件等）
        base.OnStartup(e);

        // 后台服务先行：无论是否显示界面都要开始追踪
        AppServices.EnsureInitialized();

        // 托盘宿主：始终创建（承担托盘图标与消息泵）
        Host = new TrayHost();
        Host.Show(); // 0x0 且定位屏幕外，用户不可见

        // 非 --minimized 启动 → 立即显示主窗口；--minimized → 只驻留托盘
        bool minimized = e.Args is { Length: > 0 } args &&
                         Array.Exists(args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimized)
        {
            // 正常双击启动：由托盘宿主负责创建并显示主窗口
            Host.ShowMainFromStartup();
        }

        // 记录启动完成日志（注明本次是隐藏到托盘还是显示主窗口）
        Logger.Info($"应用启动完成（{(minimized ? "隐藏到托盘" : "显示主窗口")}）");
    }

    /// <summary>
    /// 退出重写：统一停止全部后台服务（引擎/截图/调度器），确保数据完整落库。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.ShutdownAll(); // 统一停引擎/截图/调度器
        // 继续默认退出流程
        base.OnExit(e);
    }
}
