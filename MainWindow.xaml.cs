using System;
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

/// <summary>
/// 主窗口 — TimeActivity 的核心界面，包含时间轴可视化、活动列表、
/// 使用占比统计、追踪控制、托盘图标、设置入口等全部主要功能。
/// 负责协调追踪引擎、截图服务、分类器、渲染器等多个子系统。
/// </summary>
public partial class MainWindow : Window
{
    // 追踪引擎：定时轮询当前活动窗口
    private readonly TrackingEngine _engine;

    // 分类器：根据规则把进程名/窗口标题归到某个分类
    private readonly ActivityClassifier _classifier;

    // 截图服务：定时或切换应用时截屏
    private readonly ScreenshotService _screenshotService;

    // AI 总结定时调度：每天 0:00 自动生成 日/周/月 总结，启动也会补算错过的（后台线程，不阻塞 UI）
    private readonly SummaryScheduler _summaryScheduler = new();

    // 活动列表的数据源（ObservableCollection 支持双向绑定自动刷新）
    private readonly ObservableCollection<ActivityDisplayItem> _items = new();

    // 统计报表页（嵌入到 Tab 2 的 Frame 里）
    private StatisticsPage? _statsPage;

    // 分类颜色助手
    private CategoryColorHelper _colorHelper = new();

    // 时间轴渲染器和概览条渲染器
    private readonly TimelineRenderer _timelineRenderer;
    private readonly OverviewRenderer _overviewRenderer;

    // 分类名 → 颜色十六进制字符串的缓存
    private Dictionary<string, string> _categoryColors = new();

    // 当前查看的日期（默认今天）
    private DateTime _currentDate = DateTime.Today;

    // === 时间轴核心参数 ===
    // 可见时间范围（秒），1x 时 = 86400（全天）。滚轮缩放改这个值，越小越放大
    private double _visibleSeconds = 86400;

    // 缩放范围限制：最小 5 分钟，最大 24 小时
    private const double MinVisibleSeconds = 300;
    private const double MaxVisibleSeconds = 86400;

    // 可见范围起始时间（秒，0~86400-visibleSeconds）
    private double _viewStartSeconds = 0;

    // 时间轴和概览条的高度（像素）
    private const int TimelineHeight = 44;
    private const int OverviewHeight = 20;

    // 防抖定时器：活动记录频繁触发时延迟刷新，避免卡顿
    private System.Windows.Threading.DispatcherTimer? _debounceTimer;
    private const int DebounceMs = 500;

    // 自动刷新定时器：每 30 秒轻量刷新数据
    private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;

    // 当前日期的活动数据缓存
    private List<ActivityRecord> _cachedActivities = new();

    // 自动周/月总结检查日期缓存（同一天只查一次）
    private DateTime _lastAutoSummaryCheckDate = DateTime.MinValue;

    // Popup（浮动详情框）状态标志
    private bool _popupOpen = false;
    private string? _lastScreenshotPath = null; // 上次显示的截图路径，避免重复加载

    // 概览条拖拽状态
    private bool _overviewDragging = false;
    private double _dragStartX = 0;
    private double _dragStartViewStart = 0;

    // 托盘图标
    private TrayIcon? _trayIcon;
    private bool _forceClose = false; // true=真正退出，false=最小化到托盘

    // === 使用占比高亮 ===
    // 勾选高亮的应用名集合和类别名集合
    private readonly HashSet<string> _checkedApps = new();
    private readonly HashSet<string> _checkedCategories = new();

    // 唯一高亮来源：直接引用上面的集合
    private HashSet<string> ActiveAppHighlights => _checkedApps;
    private HashSet<string> ActiveCategoryHighlights => _checkedCategories;

    /// <summary>
    /// 构造函数：初始化所有子系统（数据库、分类器、追踪引擎、截图服务、
    /// 渲染器、托盘、自动刷新等），加载设置并启动追踪。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent(); // 加载 XAML，创建全部控件

        // 初始化数据库
        DatabaseHelper.Initialize(); // 建库/迁移/种子数据（幂等）
        LoadCategoryColors();        // 预载分类颜色缓存供图例与渲染使用

        // 初始化渲染器，设置颜色查找回调
        _timelineRenderer = new TimelineRenderer(_colorHelper);
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat); // 注入统一取色逻辑
        _overviewRenderer = new OverviewRenderer(_colorHelper);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat); // 概览条同款取色

        // 初始化分类器和追踪引擎
        _classifier = new ActivityClassifier();      // 从库中加载分类规则
        _engine = new TrackingEngine(_classifier);   // 轮询采样引擎
        _screenshotService = new ScreenshotService();// 截图服务（默认不启动）

        // 启动时重新分类历史数据（规则可能已更新）
        try
        {
            DatabaseHelper.ReclassifyAll(_classifier.Classify); // 全量按最新规则重算
            // 底层数据已变，使近期自动总结失效，待下方 _summaryScheduler.Start() 补算时刷新
            AISummaryRepository.InvalidateRecent();
        }
        catch (Exception ex)
        {
            Logger.Error("启动重新分类失败", ex); // 失败不阻断启动
        }

        // 从设置读取采样间隔和空闲阈值
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = Math.Clamp(poll, 1, 3600);   // 限制在 1秒~1小时
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = Math.Clamp(idle, 10, 86400); // 限制在 10秒~1天

        // 订阅追踪引擎的事件
        _engine.OnActivityRecorded += OnActivityRecorded; // 每条活动完成 → 更新列表
        _engine.OnStatusChanged += OnStatusChanged;       // 状态变化 → 更新底部文字

        // 绑定活动列表数据源
        ActivityList.ItemsSource = _items;

        // 加载颜色模式设置（按分类着色 or 按应用着色）
        _colorMode = SettingsRepository.Get("ColorMode", "category");
        if (_colorMode == "app") // 应用着色模式则选中对应单选按钮
        {
            RbColorCategory.IsChecked = false;
            RbColorApp.IsChecked = true;
        }
        AppColorAllocator.LoadFromDb(); // 预载应用颜色表

        // 画图例并加载当天数据
        DrawLegend();
        LoadDateData(_currentDate, isDateChange: true);

        // 初始化统计报表页
        _statsPage = new StatisticsPage();
        StatsFrame.Navigate(_statsPage);

        // 设置页改为独立窗口，不再在 Tab 里加载

        // 窗口大小变化时重绘时间轴 — 整体等比缩放
        TimelineContainer.SizeChanged += (s, e) =>
        {
            if (e.WidthChanged) // 只关心宽度变化（高度固定）
                DrawAll();
        };

        // 自动刷新定时器：每 30 秒轻量刷新（查库 + 重绘，不重建 ListView）
        _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _autoRefreshTimer.Tick += (s, e) =>
        {
            // 只刷新今天的数据
            if (_currentDate == DateTime.Today)
            {
                // 轻量刷新：只查库+重绘时间轴，不重建 ListView（避免卡顿）
                var activities = ActivityRepository.GetByDate(_currentDate);
                _cachedActivities = activities;

                // _items 是倒序的（最新在顶部），新记录要 Insert(0) 加到顶部
                // 用 Id 去重，避免 OnActivityRecorded 和自动刷新同时插入同一条记录
                var existingIds = new HashSet<long>(_items.Select(i => i.Id));
                var newActivities = activities.Where(a => !existingIds.Contains(a.Id)).ToList();
                if (newActivities.Count > 0)
                {
                    // activities 是正序（旧→新），倒着 Insert(0) 保持倒序
                    for (int i = newActivities.Count - 1; i >= 0; i--)
                    {
                        _items.Insert(0, CreateDisplayItem(newActivities[i]));
                    }
                    while (_items.Count > 500)
                        _items.RemoveAt(_items.Count - 1);
                }
                // 更新最新一条记录的结束时间和时长（倒序第一个=最新的，可能还在进行中）
                if (_items.Count > 0 && activities.Count > 0)
                {
                    var last = activities[activities.Count - 1];
                    var item = _items[0];
                    item.EndTime = last.EndTime;
                    item.DurationText = TimeFormatHelper.Format(last.Duration);
                }

                DrawAll();
                UpdateTodayTotal();

                // 如果停在"使用占比"模式，也刷新统计列表
                if (StatsListPanel?.Visibility == Visibility.Visible)
                    LoadStatsLists();
            }
            // 检查是否需要自动生成周/月总结
            _ = CheckAutoSummaryAsync();

            // 每次自动刷新都更新当天的汇总（一天的数据量不大，GROUP BY 很快）
            try { DailySummaryRepository.GenerateForDate(DateTime.Today.ToDateKey()); }
            catch (Exception ex) { Logger.Error("自动刷新生成每日汇总失败", ex); }
        };
        _autoRefreshTimer.Start(); // 启动 30 秒周期自动刷新

        // 启动 AI 总结定时调度（每天 0:00 自动生成 日/周/月 总结；启动时也会补算错过的任务）
        _summaryScheduler.Start();

        // 启动时执行数据保留清理（按设置的天数删旧数据）
        PerformDataRetention();

        // 启动时检查是否需要补生成上周/上月的自动总结
        _ = CheckAutoSummaryAsync();

        // 如果设置了自动开始追踪，则启动引擎和截图服务
        if (SettingsRepository.Get("AutoStartTracking", "true") == "true")
        {
            _engine.Start(); // 自动开始采样
            if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                _screenshotService.Start(); // 截图开关打开才启动
            BtnStart.IsEnabled = false;     // 按钮状态与运行中保持一致
            BtnStop.IsEnabled = true;
            StatusText.Text = "追踪中...";
        }

        // 初始化托盘需等窗口句柄就绪
        this.SourceInitialized += (s, e) => InitTray();

        // 设置窗口保存后重启截图服务
        SettingsWindow.SettingsSaved += OnSettingsSaved;

        // 切换应用时截屏（仿 ManicTime）
        _engine.OnAppSwitched += () => _screenshotService.OnAppSwitched();

        // --minimized 启动时直接隐藏到托盘
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) // 命令行带 --minimized 参数
        {
            this.SourceInitialized += (s, e) => Hide(); // 句柄就绪后立即隐藏窗口
        }
    }

    /// <summary>
    /// 设置窗口保存后回调：重启截图服务、重读追踪参数、重载规则并重新分类、刷新颜色和图表
    /// </summary>
    private void OnSettingsSaved()
    {
        // 截图服务：如果在跑就先停，然后按新设置决定是否启动
        if (_screenshotService.IsRunning)
            _screenshotService.Stop();
        if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
            _screenshotService.Start();

        // 追踪引擎重读采样间隔和空闲阈值
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = Math.Clamp(poll, 1, 3600);
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = Math.Clamp(idle, 10, 86400);

        // 分类器重载规则 + 重新分类历史数据
        _classifier.ReloadRules();
        try
        {
            DatabaseHelper.ReclassifyAll(_classifier.Classify);
            // 底层数据已变，使近期自动总结失效并立即补算刷新
            AISummaryRepository.InvalidateRecent();
            _summaryScheduler.RegenerateNow();
        }
        catch (Exception ex) { Logger.Error("OnSettingsSaved 重新分类失败", ex); }

        // 重新从数据库加载缓存，否则时间轴和统计列表读到的还是旧分类
        _cachedActivities = ActivityRepository.GetByDate(_currentDate);
        // 同步更新 _items 里的分类
        foreach (var item in _items)
        {
            var match = _cachedActivities.FirstOrDefault(a => a.ProcessName == item.ProcessName);
            if (match != null) item.Category = match.Category;
        }

        // 重载分类颜色（用户可能改了分类颜色）
        // 重新创建实例确保不残留旧缓存
        _colorHelper = new CategoryColorHelper();
        LoadCategoryColors();
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        DrawLegend();
        DrawAll();

        // 刷新统计报表页（用户可能改了分类规则/颜色）
        _statsPage?.RefreshData();

        // 执行数据保留清理
        PerformDataRetention();
    }

    // ========== 设置窗口 + 颜色模式 + 右键菜单 ==========

    // 颜色模式："category"=按分类着色，"app"=按应用着色
    private string _colorMode = "category";





    // ========== 右键菜单：应用统计右键（改颜色/改分类） ==========


    // ========== 右键菜单：类别统计右键（改颜色/查看规则） ==========


    // ========== 右键菜单辅助方法 ==========





    /// <summary>
    /// 递归查找指定类型的子元素（可视树遍历）
    /// </summary>
    /// <typeparam name="T">目标元素类型</typeparam>
    /// <param name="parent">起始父节点</param>
    /// <returns>第一个命中的子元素；不存在返回 null</returns>
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) // 遍历所有直接子级
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) // 命中直接返回
                return found;
            var result = FindChild<T>(child); // 否则递归向下找
            if (result != null)
                return result;
        }
        return null; // 整棵子树都没有
    }
}

