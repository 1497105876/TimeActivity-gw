// ============================================================================
// AppServices.cs — 后台服务中枢（静态单例集合）
// 引入原因（2026-08-23，方案A"主窗口延迟创建"）：
//   开机自启 --minimized 时不再创建 MainWindow，只有托盘宿主(TrayHost)。
//   追踪引擎/分类器/截图/总结调度必须独立于窗口存活，故上移到此处；
//   MainWindow 创建时从本类取实例（字段仍叫 _engine 等，改动面最小）。
// ============================================================================
using System;                              // Action、Math、GC、OperatingSystem
using System.Diagnostics;                  // Process（取当前进程句柄）
using System.Runtime.InteropServices;      // DllImport（工作集 / 效率模式）
using System.Threading;                    // Timer（内存优化定时器）
using TimeActivity.Data;                   // DatabaseHelper、SettingsRepository、各类 Repository

namespace TimeActivity.Services;

/// <summary>
/// 后台服务静态单例集合：分类器/追踪引擎/截图服务/总结调度的统一持有者与生命周期入口
/// </summary>
/// <remarks>
/// 这些服务必须在没有主窗口的情况下也能跑（--minimized 开机自启场景），
/// 所以不挂在 MainWindow 上，而是由本类静态持有；MainWindow 创建时反过来从这里取实例。
/// </remarks>
public static class AppServices
{
    // 分类器：规则驱动的"进程名+窗口标题 → 活动分类"，无状态可长期复用
    public static ActivityClassifier Classifier { get; private set; } = new();
    // 追踪引擎：初始化后才赋值（Initialize 内 new），此前访问为 null —— 用 default! 压制可空告警
    public static TrackingEngine Engine { get; private set; } = default!;
    // 截图服务：切换应用时截屏
    public static ScreenshotService Screenshots { get; private set; } = new();
    // 总结调度器：AI 日/周/月总结的定时与补算
    public static SummaryScheduler Scheduler { get; private set; } = new();

    // Initialize 幂等标志：防止重复初始化（App.OnStartup 只应调用一次）
    private static bool _initialized;
    // 保护初始化临界区的锁（双检锁模式）
    private static readonly object _initLock = new();
    // SettingsSaved 事件只允许订阅一次的防重入标志
    private static bool _settingsHooked;
    // 保护"只订阅一次"判定的锁；本类生命周期同进程，事件无需解绑
    private static readonly object _settingsHookLock = new();

    // 内存优化定时器：定期清理图标缓存里的死弱引用
    // 静态字段持有引用，防止定时器被 GC 回收导致回调只触发一次
    private static Timer? _memoryOptimizationTimer;

    /// <summary>
    /// 惰性初始化全部后台服务（幂等、线程安全）。
    /// 首次调用时：完成建库、按需重分类（规则指纹判定）、事件接线，并按配置启动追踪与调度器。
    /// 后续调用直接返回。
    /// </summary>
    public static void EnsureInitialized()
    {
        // 第一重快速判断：绝大多数调用（初始化之后每次）在这里直接返回，无锁开销
        if (_initialized) return;
        lock (_initLock)
        {
            // 第二重判断：并发进入的两个线程只有一个会真正执行初始化
            if (_initialized) return;
            // 先置位再干活：即使下面某步抛异常，也不会被重复初始化（异常会向外抛给 App.OnStartup）
            _initialized = true;

            // 数据库先行（各 Repository 内部也会 EnsureInit，这里显式初始化便于集中日志）
            // 建库/建表/迁移都在这里完成，后续的 Repository 调用才有表可用
            DatabaseHelper.Initialize();

            // 规则指纹：仅当规则相对上次记录变化时才全量重分类+失效近期总结
            try
            {
                // 指纹存在 Settings 表里；首次安装会判定为"已变化"，触发一次全量重分类
                if (RuleRepository.HasChangedSinceStored())
                {
                    // 用当前分类器把 Activities 表里所有历史记录重新归类
                    DatabaseHelper.ReclassifyAll(Classifier.Classify);
                    // 归类变了，基于旧数据生成的 AI 总结就作废了，等下次调度重算
                    AISummaryRepository.InvalidateRecent();
                    // 重算完成再写指纹，避免中途失败导致下次漏掉重分类
                    RuleRepository.StoreFingerprint();
                    Logger.Info("检测到分类规则变化：已全量重分类并使近期总结失效");
                }
            }
            catch (Exception ex)
            {
                // 重分类失败不影响启动：历史数据仍是旧分类，下次启动还会再试
                Logger.Error("启动按需重分类失败", ex);
            }

            // 初始化引擎与采样参数
            // 引擎内部持有分类器引用，采样时同步调用 Classify
            Engine = new TrackingEngine(Classifier);
            // 从设置读采样间隔/空闲阈值并下发给引擎
            ApplyTrackingSettings();

            // 切换应用时截屏（仿 ManicTime）
            // 静态委托常驻：Engine 与 Screenshots 都是静态单例，无需解绑
            Engine.OnAppSwitched += () => Screenshots.OnAppSwitched();

            // 设置保存后的"服务侧"处理：重启截图服务/重读参数（UI 刷新由主窗口自己订阅处理）
            HookSettingsSaved();

            // 按配置自动开始追踪（无论是否创建主窗口都要追踪！）
            // 默认值 "true"：装好就能用，符合时间追踪类工具的预期
            if (SettingsRepository.Get("AutoStartTracking", "true") == "true")
            {
                StartTracking();
                Logger.Info("已随启动自动开始追踪");
            }

            // AI 总结定时调度（每天 0:00 自动生成 日/周/月 总结；启动也会补算错过的日/周/月）
            Scheduler.Start();

            // 启动内存优化定时器：定期清理弱引用缓存、强制 GC
            StartMemoryOptimizationTimer();
        }
    }

    /// <summary>从设置读取采样间隔/空闲阈值并应用到引擎。</summary>
    public static void ApplyTrackingSettings()
    {
        // TryParse 失败（设置值损坏）时静默跳过，沿用引擎默认值
        // 默认 3 秒：再快也感知不到用户切换，再慢会漏掉短时活动
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            // 采样间隔钳制在 1 秒~1 小时，防极端值拖垮性能或失去意义
            Engine.PollIntervalSeconds = Math.Clamp(poll, 1, 3600);
        // 默认 300 秒（5 分钟）无键鼠输入即判定为空闲，与 ManicTime 的口径接近
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
            // 空闲阈值钳制在 10 秒~24 小时
            Engine.IdleThresholdSeconds = Math.Clamp(idle, 10, 86400);
    }

    /// <summary>
    /// 订阅设置保存事件的服务侧处理（只做一次）：
    /// 截图服务按新配置启停；引擎重读参数；规则变化时重算+立即补算总结。
    /// </summary>
    private static void HookSettingsSaved()
    {
        // 简单布尔标志而非锁内判定：SettingsWindow 的事件只在 UI 线程触发，不存在并发订阅
        if (_settingsHooked) return;
        _settingsHooked = true;

        // 静态事件 + 静态订阅者 = 终身引用；本类永不卸载故无泄漏风险，
        // 但也意味着退出前无法解绑（无需解绑）
        SettingsWindow.SettingsSaved += () =>
        {
            // 服务侧批量调整包整体 try：任一步失败不影响其他步骤后的日志与 UI
            try
            {
                // 截图服务：先停再按新配置决定是否启动
                // 截图默认关闭（"false"）—— 涉隐私，必须用户显式开启
                if (Screenshots.IsRunning) Screenshots.Stop();
                if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                    Screenshots.Start();

                // 引擎重读采样参数（用户可能刚改了采样间隔/空闲阈值）
                ApplyTrackingSettings();

                // 仅当分类规则真正变化时才重算历史数据并补算总结（规则指纹机制）
                // 无条件 ReloadRules：用户也可能只改了颜色/其他设置，重载代价很低
                Classifier.ReloadRules();
                if (RuleRepository.HasChangedSinceStored())
                {
                    // 历史记录全部按新规则重新归类
                    DatabaseHelper.ReclassifyAll(Classifier.Classify);
                    // 旧总结已过时，标记失效
                    AISummaryRepository.InvalidateRecent();
                    // 立即触发一次总结重算，不必等到次日 0:00
                    Scheduler.RegenerateNow();
                    // 指纹最后写：保证中途失败时下次仍能重试
                    RuleRepository.StoreFingerprint();
                    Logger.Info("设置保存：检测到规则变化，已重分类并刷新总结");
                }
            }
            catch (Exception ex)
            {
                // 事件处理器里抛异常会冒泡到设置窗口的保存流程，这里统一吞掉只记日志
                Logger.Error("AppServices 处理 SettingsSaved 失败", ex);
            }
        };
    }

    /// <summary>开始追踪（托盘菜单/主窗口按钮共用），返回是否实际启动。</summary>
    public static bool StartTracking()
    {
        // 已在运行则幂等返回 false，调用方据此刷新按钮状态
        // 注意：此时 Engine 必须已由 EnsureInitialized 赋值，否则这里会 NullReferenceException
        if (Engine.IsRunning) return false;
        // 启动后台采样线程
        Engine.Start();
        // 截图是跟随项：只有在设置里开启过才随追踪一起启动
        if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
            Screenshots.Start();
        // true = 本次调用真的启动了追踪
        return true;
    }

    /// <summary>停止追踪与截图（托盘菜单/主窗口按钮共用）。</summary>
    public static void StopTracking()
    {
        // 停采样线程；已停止时引擎内部自行幂等处理
        Engine.Stop();
        // 截图随追踪一起停（即使截图开关为 true 也停，符合直觉）
        Screenshots.Stop();
    }

    /// <summary>应用退出时的统一清理：停全部后台服务。</summary>
    public static void ShutdownAll()
    {
        // 退出路径上每个 Stop 都单独吞异常：保证前面的失败不阻断后续清理
        // 注意：未 Dispose _memoryOptimizationTimer，进程退出时由运行时回收
        try { Engine.Stop(); } catch { /* 退出路径尽量不抛 */ }
        try { Screenshots.Stop(); } catch { }
        try { Scheduler.Stop(); } catch { }
    }

    /// <summary>
    /// 启动内存优化定时器：定期清理弱引用缓存。
    /// 2026-08-25 调整：移除周期性的强制 Gen2 GC —— WPF 桌面应用中周期性 Full GC
    /// 会引发 UI 停顿且制造"内存下降"假象（真实内存由常驻对象与分配量决定），
    /// 保留弱引用清理即可；内存回收交给 GC 硬限制与 P0 层的对象释放。
    /// </summary>
    private static void StartMemoryOptimizationTimer()
    {
        // 每 5 分钟清理一次死弱引用
        // 用 System.Threading.Timer：回调在线程池线程执行，不占 UI 线程
        _memoryOptimizationTimer = new Timer(
            callback: _ =>
            {
                try
                {
                    // 只清图标缓存里已失效的弱引用条目，不做强制 GC（见方法说明）
                    IconExtractor.CleanupDeadReferences();
                }
                catch (Exception ex)
                {
                    // 线程池回调里未捕获的异常会直接崩进程，必须兜住
                    Logger.Error("内存优化定时器异常", ex);
                }
            },
            state: null,
            dueTime: TimeSpan.FromMinutes(5),   // 启动 5 分钟后才首次触发，避开启动期的高分配
            period: TimeSpan.FromMinutes(5)     // 之后每 5 分钟一次
        );
    }

    /// <summary>
    /// 当窗口最小化到托盘/关闭销毁时调用：释放工作集、触发压缩回收、启用效率模式
    /// </summary>
    public static void OnMinimizedToTray()
    {
        try
        {
            // 释放工作集（将内存页移至磁盘）
            // 传 -1（0xFFFFFFFF）是约定用法：让系统把工作集尽可能清空，任务管理器里的内存立刻下降
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (UIntPtr)0xFFFFFFFF, (UIntPtr)0xFFFFFFFF);
            // 事件驱动的一次性强制 Gen2 GC + 堆压缩（2026-08-25 由 Optimized 改为 Forced+压缩：
            // 窗口刚销毁的场景需要内存立刻回落，短暂停顿可接受；非周期调用无累积开销）
            // 第 3、4 个参数：blocking=true、compacting=true
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            // 等终结器线程跑完，确保 GDI/文件句柄等非托管资源真正释放
            GC.WaitForPendingFinalizers();
            // Windows 11+ 效率模式
            // 22000 是 Win11 首个正式版的内部版本号；低于此版本该 API 不存在，必须跳过
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                // 传 1 = 启用效率模式（降低后台进程的 CPU 优先级与功耗）
                SetProcessDefaultEfficiencyMode(Process.GetCurrentProcess().Handle, 1); // PROCESS_POWER_THROTTLING
            }
            Logger.Info("已最小化到托盘，已释放工作集并触发 GC");
        }
        catch (Exception ex)
        {
            // 内存优化失败对功能无影响，只记日志
            Logger.Error("最小化到托盘时内存优化失败", ex);
        }
    }

    // 设置进程工作集大小；(UIntPtr)-1 表示"清空工作集"
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, UIntPtr dwMinimumWorkingSetSize, UIntPtr dwMaximumWorkingSetSize);

    // 设置进程默认效率模式（Windows 11+）；value: 0=关闭，1=开启
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultEfficiencyMode(IntPtr hProcess, int value);
                }
