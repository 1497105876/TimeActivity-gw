// ============================================================================
// AppServices.cs — 后台服务中枢（静态单例集合）
// 引入原因（2026-08-23，方案A"主窗口延迟创建"）：
//   开机自启 --minimized 时不再创建 MainWindow，只有托盘宿主(TrayHost)。
//   追踪引擎/分类器/截图/总结调度必须独立于窗口存活，故上移到此处；
//   MainWindow 创建时从本类取实例（字段仍叫 _engine 等，改动面最小）。
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// 后台服务静态单例集合：分类器/追踪引擎/截图服务/总结调度的统一持有者与生命周期入口
/// </summary>
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
    private static readonly object _initLock = new();
    // SettingsSaved 事件只允许订阅一次的防重入标志
    private static bool _settingsHooked;
    private static readonly object _settingsHookLock = new();

    // 内存优化定时器
    private static Timer? _memoryOptimizationTimer;

    /// <summary>
    /// 惰性初始化全部后台服务（幂等、线程安全）。
    /// 首次调用时：完成建库、按需重分类（规则指纹判定）、事件接线，并按配置启动追踪与调度器。
    /// 后续调用直接返回。
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;

            // 数据库先行（各 Repository 内部也会 EnsureInit，这里显式初始化便于集中日志）
            DatabaseHelper.Initialize();

            // 规则指纹：仅当规则相对上次记录变化时才全量重分类+失效近期总结
            try
            {
                if (RuleRepository.HasChangedSinceStored())
                {
                    DatabaseHelper.ReclassifyAll(Classifier.Classify);
                    AISummaryRepository.InvalidateRecent();
                    RuleRepository.StoreFingerprint();
                    Logger.Info("检测到分类规则变化：已全量重分类并使近期总结失效");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("启动按需重分类失败", ex);
            }

            // 初始化引擎与采样参数
            Engine = new TrackingEngine(Classifier);
            ApplyTrackingSettings();

            // 切换应用时截屏（仿 ManicTime）
            Engine.OnAppSwitched += () => Screenshots.OnAppSwitched();

            // 设置保存后的"服务侧"处理：重启截图服务/重读参数（UI 刷新由主窗口自己订阅处理）
            HookSettingsSaved();

            // 按配置自动开始追踪（无论是否创建主窗口都要追踪！）
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
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            // 采样间隔钳制在 1 秒~1 小时，防极端值拖垮性能或失去意义
            Engine.PollIntervalSeconds = Math.Clamp(poll, 1, 3600);
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
                if (Screenshots.IsRunning) Screenshots.Stop();
                if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                    Screenshots.Start();

                // 引擎重读采样参数
                ApplyTrackingSettings();

                // 仅当分类规则真正变化时才重算历史数据并补算总结（规则指纹机制）
                Classifier.ReloadRules();
                if (RuleRepository.HasChangedSinceStored())
                {
                    DatabaseHelper.ReclassifyAll(Classifier.Classify);
                    AISummaryRepository.InvalidateRecent();
                    // 立即触发一次总结重算，不必等到次日 0:00
                    Scheduler.RegenerateNow();
                    RuleRepository.StoreFingerprint();
                    Logger.Info("设置保存：检测到规则变化，已重分类并刷新总结");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AppServices 处理 SettingsSaved 失败", ex);
            }
        };
    }

    /// <summary>开始追踪（托盘菜单/主窗口按钮共用），返回是否实际启动。</summary>
    public static bool StartTracking()
    {
        // 已在运行则幂等返回 false，调用方据此刷新按钮状态
        if (Engine.IsRunning) return false;
        Engine.Start();
        if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
            Screenshots.Start();
        return true;
    }

    /// <summary>停止追踪与截图（托盘菜单/主窗口按钮共用）。</summary>
    public static void StopTracking()
    {
        Engine.Stop();
        // 截图随追踪一起停（即使截图开关为 true 也停，符合直觉）
        Screenshots.Stop();
    }

    /// <summary>应用退出时的统一清理：停全部后台服务。</summary>
    public static void ShutdownAll()
    {
        // 退出路径上每个 Stop 都单独吞异常：保证前面的失败不阻断后续清理
        try { Engine.Stop(); } catch { /* 退出路径尽量不抛 */ }
        try { Screenshots.Stop(); } catch { }
        try { Scheduler.Stop(); } catch { }
    }

    /// <summary>
    /// 启动内存优化定时器：定期清理弱引用缓存、强制 GC
    /// </summary>
    private static void StartMemoryOptimizationTimer()
    {
        // 每 5 分钟清理一次死弱引用，每 10 分钟强制一次 Gen2 GC
        _memoryOptimizationTimer = new Timer(
            callback: _ =>
            {
                try
                {
                    IconExtractor.CleanupDeadReferences();
                    // 每 2 次清理触发一次 Gen2 GC（约每 10 分钟）
                    if (DateTime.Now.Minute % 10 == 0)
                    {
                        GC.Collect(2, GCCollectionMode.Optimized);
                        GC.WaitForPendingFinalizers();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("内存优化定时器异常", ex);
                }
            },
            state: null,
            dueTime: TimeSpan.FromMinutes(5),
            period: TimeSpan.FromMinutes(5)
        );
    }

    /// <summary>
    /// 当窗口最小化到托盘时调用：释放工作集、触发 GC、启用效率模式
    /// </summary>
    public static void OnMinimizedToTray()
    {
        try
        {
            // 释放工作集（将内存页移至磁盘）
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (UIntPtr)0xFFFFFFFF, (UIntPtr)0xFFFFFFFF);
            // 强制 Gen2 GC 回收托管内存
            GC.Collect(2, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
            // Windows 11+ 效率模式
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                SetProcessDefaultEfficiencyMode(Process.GetCurrentProcess().Handle, 1); // PROCESS_POWER_THROTTLING
            }
            Logger.Info("已最小化到托盘，已释放工作集并触发 GC");
        }
        catch (Exception ex)
        {
            Logger.Error("最小化到托盘时内存优化失败", ex);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, UIntPtr dwMinimumWorkingSetSize, UIntPtr dwMaximumWorkingSetSize);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultEfficiencyMode(IntPtr hProcess, int value);
                }
