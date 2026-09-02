// ============================================================================
// App.xaml.cs — 应用入口类（2026-08-23 方案A 重写启动流程；2026-08-27 加单实例守卫）
// 流程：
//   0) 单实例检测（Global\ 命名互斥体，跨会话可见）：已有一个实例则提示并退出，
//      防止 BetterGI 桌面分身（本机 RDP 会话）里启动第二实例污染共享数据库/截图目录；
//   1) AppServices.EnsureInitialized()：建库/按需重分类/创建并按配置启动后台服务；
//   2) 创建托盘宿主 TrayHost（0x0 屏幕外窗口，承担托盘图标与消息）；
//   3) 仅当非 --minimized 启动时才立即创建并显示 MainWindow；
//      --minimized（开机自启）时只驻留托盘，首次点击托盘再延迟创建主窗口。
//   退出时 OnExit 统一释放互斥体并停止全部后台服务。
// ============================================================================
using System;
using System.Threading;
using System.Windows;
using TimeActivity.Services;

namespace TimeActivity;

public partial class App : Application
{
    /// <summary>托盘宿主引用（供主窗口刷新托盘提示等）。</summary>
    public TrayHost? Host { get; private set; }

    // ==================== 全局单实例互斥体（2026-08-27） ====================
    // 背景：BetterGI"桌面分身"（本机 RDP 远程会话）会在分身桌面上再次启动本程序，
    // 两个实例同时读写同一个 timeactivity.db 与 screenshots/ 目录：
    //   - 分身实例把分身会话里的活动（如原神）和截图写入共享库 → 主实例时间轴被污染；
    //   - 主实例的 GetForegroundWindow/GetLastInputInfo 看不到分身会话 → 误判空闲/长活动。
    // 解决：Global\ 前缀命名互斥体跨 Windows 会话可见，分身实例启动时能命中主实例。
    // 仅 createdNew=true 的第一个实例把互斥体存入此字段，退出时 Release；
    // 未持有者字段保持 null，ReleaseSingleInstance 直接跳过（避免对未拥有互斥体调用 ReleaseMutex 抛异常）
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// 尝试获取全局单实例互斥体。
    /// </summary>
    /// <returns>true = 本实例是唯一实例（已持有互斥体，可继续启动）；false = 已有实例在运行。</returns>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            // Global\ 前缀：跨会话共享命名空间 —— 物理桌面与 RDP 分身会话互相可见
            var mutex = new Mutex(initiallyOwned: true, @"Global\TimeActivity_SingleInstance", out bool createdNew);
            if (createdNew)
            {
                // 第一个实例：持有互斥体（存入字段，退出时 Release）
                _singleInstanceMutex = mutex;
                return true;
            }
            // 已有实例持有：本实例不拥有，直接释放句柄并退出
            mutex.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // 个别受限环境无法创建 Global\ 命名对象：降级为会话级互斥体
            // （至少能挡同桌面/同会话内的双开；跨会话场景退回原行为，不阻塞启动）
            try
            {
                var mutex = new Mutex(initiallyOwned: true, @"TimeActivity_SingleInstance", out bool createdNew);
                if (createdNew)
                {
                    _singleInstanceMutex = mutex;
                    return true;
                }
                mutex.Dispose();
                return false;
            }
            catch
            {
                return true; // 极端失败不阻塞启动（宁可双开也不拒绝运行）
            }
        }
    }

    /// <summary>释放单实例互斥体：仅当本实例真正持有（createdNew=true，字段非空）时才 Release。</summary>
    private static void ReleaseSingleInstance()
    {
        try
        {
            // 字段只在创建成功（createdNew=true）时被赋值，因此走到这里必然持有，ReleaseMutex 安全
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("释放单实例互斥体失败", ex);
        }
    }

    /// <summary>
    /// 启动入口重写：单实例守卫 → 初始化后台服务 → 创建托盘宿主 → 按命令行参数决定是否立即显示主窗口。
    /// </summary>
    /// <param name="e">启动参数（可含 --minimized 表示开机自启静默驻留）</param>
protected override void OnStartup(StartupEventArgs e)
{
    // 单实例守卫（2026-08-27）：放在一切初始化之前 —— 已有一个实例（含其他 RDP 会话）时
    // 提示后直接退出，不建库、不启动任何服务，避免与既有实例竞争同一数据库/截图目录。
    if (!TryAcquireSingleInstance())
    {
        // 注意：本实例未持有互斥体（createdNew=false），不能也不需 Release
        Logger.Info("检测到 TimeActivity 已在运行，本实例退出（单实例保护）");
        MessageBox.Show("TimeActivity 已在运行（可能在其他桌面会话中）。\n本实例将退出，请使用已运行的那个。",
            "TimeActivity", MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(); // 触发 OnExit 后退出；AppServices 尚未初始化，ShutdownAll 内 try-catch 兜底
        return;
    }

    // 先执行 WPF 默认启动流程（触发 Startup 事件等）
    base.OnStartup(e);

    // 全局未捕获异常处理
    this.DispatcherUnhandledException += (s, e) =>
    {
        Logger.Error("UI 线程未捕获异常", e.Exception);
        e.Handled = true; // 防止进程崩溃
    };
    AppDomain.CurrentDomain.UnhandledException += (s, e) =>
    {
        Logger.Error("非 UI 线程未捕获异常", e.ExceptionObject as Exception);
    };

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
    /// 退出重写：释放单实例互斥体并统一停止全部后台服务（引擎/截图/调度器），确保数据完整落库。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();   // 释放互斥体（2026-08-27）：本实例退出后允许新实例启动
        AppServices.ShutdownAll(); // 统一停引擎/截图/调度器
        // 继续默认退出流程
        base.OnExit(e);
    }
}
