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

    // 托盘图标已上移到 TrayHost（2026-08-23 方案A）；窗口只保留强制退出标志
    private bool _forceClose = false; // true=真正退出，false=最小化到托盘

    // === 使用占比高亮 ===
    // 勾选高亮的应用名集合和类别名集合
    private readonly HashSet<string> _checkedApps = new();
    private readonly HashSet<string> _checkedCategories = new();

    // 唯一高亮来源：直接引用上面的集合
    private HashSet<string> ActiveAppHighlights => _checkedApps;
    private HashSet<string> ActiveCategoryHighlights => _checkedCategories;

    /// <summary>
    /// 构造函数：初始化界面侧子系统（渲染器/颜色/统计页/托盘事件订阅等）。
    /// 2026-08-23 方案A：后台服务（引擎/分类器/截图/调度器）已上移到 AppServices，
    /// 此处仅取引用并接线，不再负责建库与启动追踪 —— 因此窗口可以延迟创建。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent(); // 加载 XAML，创建全部控件

        LoadCategoryColors(); // 预载分类颜色缓存供图例与渲染使用

        // 初始化渲染器，设置颜色查找回调
        _timelineRenderer = new TimelineRenderer(_colorHelper);
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat); // 注入统一取色逻辑
        _overviewRenderer = new OverviewRenderer(_colorHelper);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat); // 概览条同款取色

        // 后台服务取自 AppServices（App 启动时已创建并按配置运行）
        _classifier = AppServices.Classifier;
        _engine = AppServices.Engine;
        _screenshotService = AppServices.Screenshots;
        _summaryScheduler = AppServices.Scheduler;

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

        // （调度器/自动开始追踪已上移 AppServices：窗口未创建时也要持续追踪与总结）

        // 启动时执行数据保留清理（按设置的天数删旧数据）
        PerformDataRetention();

        // 启动时检查是否需要补生成上周/上月的自动总结
        _ = CheckAutoSummaryAsync();

        // 设置窗口保存后刷新界面侧（服务侧处理在 AppServices.HookSettingsSaved）
        SettingsWindow.SettingsSaved += OnSettingsSaved;

        // 按钮初始状态与服务实际状态对齐（引擎可能早已随启动运行）
        RefreshTrackingButtons();
    }

    /// <summary>按引擎当前运行状态同步 开始/停止按钮 与 状态文字。</summary>
    public void RefreshTrackingButtons()
    {
        bool running = _engine.IsRunning;
        BtnStart.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        StatusText.Text = running ? "追踪中..." : "已停止";
    }

    /// <summary>托盘"退出"调用：置强制退出标志后正常走关闭流程。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>
    /// 窗口关闭（非隐藏）时解除对全局服务的订阅并停掉窗口定时器，
    /// 防止旧窗口实例泄漏或重复刷新（方案A 下主窗口可能被多次创建/关闭）。
    /// </summary>
    public void DetachFromServices()
    {
        _engine.OnActivityRecorded -= OnActivityRecorded;
        _engine.OnStatusChanged -= OnStatusChanged;
        SettingsWindow.SettingsSaved -= OnSettingsSaved;
        _autoRefreshTimer?.Stop();
        _debounceTimer?.Stop();
        Logger.Info("主窗口已关闭：解除服务事件订阅");
    }

    /// <summary>
    /// 设置窗口保存后的"界面侧"刷新：颜色缓存、图例、时间轴、统计列表与报表页。
    /// 服务侧（截图启停/参数重读/规则变化重算）由 AppServices 统一处理，勿在此重复。
    /// </summary>
    private void OnSettingsSaved()
    {
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

