// ============================================================================
// AppServices.cs — 后台服务中枢（静态单例集合）
// 引入原因（2026-08-23，方案A"主窗口延迟创建"）：
//   开机自启 --minimized 时不再创建 MainWindow，只有托盘宿主(TrayHost)。
//   追踪引擎/分类器/截图/总结调度必须独立于窗口存活，故上移到此处；
//   MainWindow 创建时从本类取实例（字段仍叫 _engine 等，改动面最小）。
// ============================================================================
using System;
using TimeActivity.Data;

namespace TimeActivity.Services;

public static class AppServices
{
    public static ActivityClassifier Classifier { get; private set; } = new();
    public static TrackingEngine Engine { get; private set; } = default!;
    public static ScreenshotService Screenshots { get; private set; } = new();
    public static SummaryScheduler Scheduler { get; private set; } = new();

    private static bool _initialized;
    private static bool _settingsHooked;

    /// <summary>
    /// 初始化全部后台服务（幂等）。在 App.OnStartup 最先调用；
    /// 完成建库、按需重分类（规则指纹判定）、事件接线，并按配置启动追踪与调度器。
    /// </summary>
    public static void Initialize()
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

        // 引擎与采样参数
        Engine = new TrackingEngine(Classifier);
        ApplyTrackingSettings();

        // 切换应用时截屏（仿 ManicTime）
        Engine.OnAppSwitched += () => Screenshots.OnAppSwitched();

        // 设置保存后的"服务侧"处理：重启截图服务/重读参数（UI 刷新由窗口自己订阅处理）
        HookSettingsSaved();

        // 按配置自动开始追踪（无论是否创建主窗口都要追踪！）
        if (SettingsRepository.Get("AutoStartTracking", "true") == "true")
        {
            Engine.Start();
            if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                Screenshots.Start();
            Logger.Info("已随启动自动开始追踪");
        }

        // AI 总结调度（每天 0:00；启动补算错过的日/周/月）
        Scheduler.Start();
    }

    /// <summary>从设置读取采样间隔/空闲阈值并应用到引擎。</summary>
    public static void ApplyTrackingSettings()
    {
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            Engine.PollIntervalSeconds = Math.Clamp(poll, 1, 3600);
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
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

        SettingsWindow.SettingsSaved += () =>
        {
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
        Screenshots.Stop();
    }

    /// <summary>应用退出时的统一清理：停全部后台服务。</summary>
    public static void ShutdownAll()
    {
        try { Engine.Stop(); } catch { /* 退出路径尽量不抛 */ }
        try { Screenshots.Stop(); } catch { }
        try { Scheduler.Stop(); } catch { }
    }
}
