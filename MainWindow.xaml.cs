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
        InitializeComponent();

        // 初始化数据库
        DatabaseHelper.Initialize();
        LoadCategoryColors();

        // 初始化渲染器，设置颜色查找回调
        _timelineRenderer = new TimelineRenderer(_colorHelper);
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        _overviewRenderer = new OverviewRenderer(_colorHelper);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);

        // 初始化分类器和追踪引擎
        _classifier = new ActivityClassifier();
        _engine = new TrackingEngine(_classifier);
        _screenshotService = new ScreenshotService();

        // 启动时重新分类历史数据（规则可能已更新）
        try
        {
            DatabaseHelper.ReclassifyAll(_classifier.Classify);
        }
        catch (Exception ex)
        {
            Logger.Error("启动重新分类失败", ex);
        }

        // 从设置读取采样间隔和空闲阈值
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = Math.Clamp(poll, 1, 3600);
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = Math.Clamp(idle, 10, 86400);

        // 订阅追踪引擎的事件
        _engine.OnActivityRecorded += OnActivityRecorded;
        _engine.OnStatusChanged += OnStatusChanged;

        // 绑定活动列表数据源
        ActivityList.ItemsSource = _items;

        // 加载颜色模式设置（按分类着色 or 按应用着色）
        _colorMode = SettingsRepository.Get("ColorMode", "category");
        if (_colorMode == "app")
        {
            RbColorCategory.IsChecked = false;
            RbColorApp.IsChecked = true;
        }
        AppColorAllocator.LoadFromDb();

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
            if (e.WidthChanged)
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
        _autoRefreshTimer.Start();

        // 启动时执行数据保留清理（按设置的天数删旧数据）
        PerformDataRetention();

        // 启动时检查是否需要补生成上周/上月的自动总结
        _ = CheckAutoSummaryAsync();

        // 如果设置了自动开始追踪，则启动引擎和截图服务
        if (SettingsRepository.Get("AutoStartTracking", "true") == "true")
        {
            _engine.Start();
            if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                _screenshotService.Start();
            BtnStart.IsEnabled = false;
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
        if (args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            this.SourceInitialized += (s, e) => Hide();
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
        try { DatabaseHelper.ReclassifyAll(_classifier.Classify); }
        catch (Exception ex) { Logger.Error("OnSettingsSaved 重新分类失败", ex); }

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

    /// <summary>
    /// 打开设置窗口
    /// </summary>
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new SettingsWindow();
            win.Owner = this;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开设置失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开设置窗口并定位到指定分区
    /// </summary>
    private void OpenSettings(string section)
    {
        try
        {
            var win = new SettingsWindow(section);
            win.Owner = this;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开设置失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 颜色模式切换（按分类/按应用）事件处理：保存设置、刷新颜色和图表
    /// </summary>
    private void ColorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _colorMode = RbColorApp.IsChecked == true ? "app" : "category";
        SettingsRepository.Set("ColorMode", _colorMode);
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        LoadCategoryColors(); // 重新加载颜色
        DrawLegend();
        DrawAll();
        // 刷新统计列表
        LoadStatsLists();
    }

    /// <summary>
    /// 根据当前颜色模式获取某个应用的颜色：按应用模式用应用色，否则用分类色
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="category">分类名</param>
    /// <returns>WPF Color 对象</returns>
    private Color GetAppColor(string processName, string category)
    {
        if (_colorMode == "app")
        {
            // 按应用着色：从分配器获取或自动分配一个颜色
            var hex = AppColorAllocator.GetOrAssign(processName);
            return CategoryColorHelper.ParseHex(hex);
        }
        // 按分类着色
        return _colorHelper.GetColor(category);
    }

    // ========== 右键菜单：应用统计右键（改颜色/改分类） ==========

    /// <summary>
    /// 应用统计列表右键菜单：提供"颜色"和"更改类别"两个操作
    /// </summary>
    private void AppStatsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(AppStatsList);

            // 找点击的行
            var item = GetListViewItemFromPoint(AppStatsList, pos);
            if (item == null) return;

            // 从行中提取进程名
            // 找到点击的行对应的进程名
            string? processName = GetTagFromStatsRow(item);
            if (string.IsNullOrEmpty(processName)) return;

            var menu = new ContextMenu();

            // 菜单项 1：修改应用颜色
            var miColor = new MenuItem { Header = "颜色" };
            miColor.Click += (s, ev) =>
            {
                try
                {
                    var current = AppColorAllocator.GetOrAssign(processName);
                    var hex = PickColor(current);
                    if (hex != null)
                    {
                        AppColorAllocator.SetCustom(processName, hex);
                        DrawAll();
                        LoadStatsLists();
                    }
                }
                catch (Exception ex) { MessageBox.Show($"颜色选择失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            menu.Items.Add(miColor);

            // 菜单项 2：更改应用所属分类
            var miCategory = new MenuItem { Header = "更改类别" };
            miCategory.Click += (s, ev) =>
            {
                try
                {
                    // 弹出分类选择小窗口，列出所有非空闲分类
                    var cats = CategoryRepository.GetAll();
                    var selWin = new Window
                    {
                        Title = $"将「{processName}」改到哪个类别？",
                        Width = 320,
                        SizeToContent = SizeToContent.Height,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        ResizeMode = ResizeMode.NoResize
                    };
                    var panel = new StackPanel { Margin = new Thickness(12) };
                    foreach (var cat in cats.Where(c => c.Name != "空闲"))
                    {
                        var btn = new Button
                        {
                            Content = cat.Name,
                            Tag = cat,
                            Margin = new Thickness(0, 0, 0, 6),
                            Padding = new Thickness(12, 6, 12, 6),
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };
                        btn.Click += (s2, e2) =>
                        {
                            var selected = (Category)((Button)s2).Tag;
                            RuleRepository.UpdateCategory(processName, selected.Id);
                            _classifier.ReloadRules();
                            try { DatabaseHelper.ReclassifyAll(_classifier.Classify); }
                            catch (Exception ex) { Logger.Error("ReclassifyAll 失败", ex); }
                            // 重新从数据库加载缓存，否则 LoadStatsLists 读到的还是旧分类
                            _cachedActivities = ActivityRepository.GetByDate(_currentDate);
                            // 同步更新 _items 里的分类
                            foreach (var item in _items)
                            {
                                var match = _cachedActivities.FirstOrDefault(a => a.ProcessName == item.ProcessName);
                                if (match != null) item.Category = match.Category;
                            }
                            DrawAll();
                            LoadStatsLists();
                            UpdateTodayTotal();
                            selWin.Close();
                            ShowStatus($"已将「{processName}」改到「{selected.Name}」");
                        };
                        panel.Children.Add(btn);
                    }
                    selWin.Content = panel;
                    selWin.ShowDialog();
                }
                catch (Exception ex) { MessageBox.Show($"更改类别失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            menu.Items.Add(miCategory);

            menu.IsOpen = true;
        }
        catch (Exception ex) { MessageBox.Show($"右键菜单失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ========== 右键菜单：类别统计右键（改颜色/查看规则） ==========

    /// <summary>
    /// 类别统计列表右键菜单：提供"颜色"（跳转设置）和"查看类别"（跳转规则）
    /// </summary>
    private void CategoryStatsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var item = GetListViewItemFromPoint(CategoryStatsList, e.GetPosition(CategoryStatsList));
            if (item == null) return;

            string? categoryName = GetTagFromStatsRow(item);
            if (string.IsNullOrEmpty(categoryName)) return;

            var menu = new ContextMenu();

            var miColor = new MenuItem { Header = "颜色" };
            miColor.Click += (s, ev) =>
            {
                _categoryColors.TryGetValue(categoryName, out var hex);
                var newHex = PickColor(hex);
                if (newHex != null)
                {
                    CategoryRepository.UpdateColor(categoryName, newHex);
                    LoadCategoryColors();
                    _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
                    _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
                    DrawLegend();
                    DrawAll();
                    LoadStatsLists();
                    _statsPage?.RefreshData();
                }
            };
            menu.Items.Add(miColor);

            var miView = new MenuItem { Header = "查看类别" };
            miView.Click += (s, ev) => OpenSettings("rules");
            menu.Items.Add(miView);

            menu.IsOpen = true;
        }
        catch (Exception ex) { MessageBox.Show($"右键菜单失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ========== 右键菜单辅助方法 ==========

/// <summary>
/// 弹出颜色选择对话框，返回选中的 hex 颜色（如 #FF0000），取消返回 null
/// </summary>
private static string? PickColor(string? currentHex = null)
{
    using var dlg = new System.Windows.Forms.ColorDialog();
    dlg.FullOpen = true;
    if (!string.IsNullOrEmpty(currentHex))
    {
        try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(currentHex); }
        catch (Exception ex) { Logger.Error("颜色解析失败", ex); }
    }
    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        return $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
    return null;
}

    /// <summary>
    /// 根据鼠标点击位置找到对应的 ListViewItem（可视树命中测试）
    /// </summary>
    private static object? GetListViewItemFromPoint(System.Windows.Controls.ListView list, System.Windows.Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.ListViewItem)
            hit = VisualTreeHelper.GetParent(hit);
        return hit;
    }

    /// <summary>
    /// 把 ActivityRecord 转成列表绑定的 ActivityDisplayItem
    /// </summary>
    private static ActivityDisplayItem CreateDisplayItem(ActivityRecord a)
    {
        return new ActivityDisplayItem
        {
            Id = a.Id,
            Icon = IconExtractor.GetIcon(a.ProcessName),
            ProcessName = a.ProcessName,
            DisplayName = Services.AppDisplayName.Get(a.ProcessName),
            WindowTitle = a.WindowTitle,
            Category = a.Category,
            StartTime = a.StartTime,
            EndTime = a.EndTime,
            DurationText = TimeFormatHelper.Format(a.Duration)
        };
    }

    /// <summary>
    /// 从统计行中提取进程名（存在 Border.Tag 里）
    /// </summary>
    private static string? GetTagFromStatsRow(object item)
    {
        // item 是 ListViewItem，里面包裹的是 Border（CreateStatsRow 返回的）
        if (item is System.Windows.DependencyObject d)
        {
            var border = FindChild<Border>(d);
            if (border?.Tag is string s)
                return s;
            // Border 可能直接就是 item 的 Content
            if (item is System.Windows.Controls.ContentControl cc && cc.Content is Border b && b.Tag is string s2)
                return s2;
        }
        return null;
    }

    /// <summary>
    /// 递归查找指定类型的子元素（可视树遍历）
    /// </summary>
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var result = FindChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    /// <summary>
    /// 按设置的数据保留天数清理旧数据
    /// </summary>
    private void PerformDataRetention()
    {
        try
        {
            string? retentionStr = SettingsRepository.Get("DataRetentionDays", "90");
            if (int.TryParse(retentionStr, out int days) && days > 0)
            {
                int deleted = DatabaseHelper.CleanOldData(days);
                if (deleted > 0)
                {
                    Logger.Info($"数据清理：删除 {deleted} 条超过 {days} 天的旧数据");
                    ShowStatus($"已清理 {deleted} 条旧数据");
                }
            }

            // 启动时补全所有缺失的每日汇总
            DailySummaryRepository.GenerateAllMissing();
        }
        catch (Exception ex)
        {
            Logger.Error("数据清理/汇总失败", ex);
        }
    }

    /// <summary>
    /// 检查是否需要自动生成周/月总结（启动兆底 + 定时检查）。
    /// 同一天只查一次，避免 30 秒刷新重复查库。
    /// </summary>
    private async Task CheckAutoSummaryAsync()
    {
        // 同一天只执行一次，避免 30 秒刷新重复查库
        if (_lastAutoSummaryCheckDate == DateTime.Today) return;
        _lastAutoSummaryCheckDate = DateTime.Today;
        Logger.Info("开始检查自动周/月总结...");

        try
        {
            if (SettingsRepository.Get("EnableAI", "true") != "true")
            {
                Logger.Info("AI 未启用，跳过自动总结");
                return;
            }

            var aiService = new AISummaryService();
            DateTime today = DateTime.Today;

            // 检查上周总结
            DateTime lastWeekStart = DateHelper.GetWeekStart(today.AddDays(-7));
            Logger.Info($"检查上周总结：lastWeekStart={lastWeekStart:yyyy-MM-dd}, HasAuto={AISummaryRepository.HasAuto(lastWeekStart, "weekly")}");
            if (!AISummaryRepository.HasAuto(lastWeekStart, "weekly"))
            {
                int weekSeconds = ActivityRepository.GetCategorySummaryByRange(lastWeekStart, lastWeekStart.AddDays(6), false).Values.Sum();
                if (weekSeconds > 0)
                {
                    Logger.Info($"补生成上周总结：{lastWeekStart:yyyy-MM-dd}");
                    string? result = await aiService.GenerateWeeklySummary(lastWeekStart);
                    Logger.Info($"上周总结生成结果：{(result != null ? "成功" : "null")}");
                    if (result != null)
                        AISummaryRepository.Insert(lastWeekStart, result, "weekly", "auto");
                }
                else
                {
                    // 没有活动数据也存一条，避免切过去显示"正在进行"
                    AISummaryRepository.Insert(lastWeekStart, "本周没有活动记录。", "weekly", "auto");
                }
            }

            // 检查上月总结
            DateTime lastMonthStart = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            Logger.Info($"检查上月总结：lastMonthStart={lastMonthStart:yyyy-MM-dd}, HasAuto={AISummaryRepository.HasAuto(lastMonthStart, "monthly")}");
            if (!AISummaryRepository.HasAuto(lastMonthStart, "monthly"))
            {
                int monthSeconds = ActivityRepository.GetCategorySummaryByRange(lastMonthStart, lastMonthStart.AddMonths(1).AddDays(-1), false).Values.Sum();
                if (monthSeconds > 0)
                {
                    Logger.Info($"补生成上月总结：{lastMonthStart:yyyy-MM-dd}");
                    string? result = await aiService.GenerateMonthlySummary(lastMonthStart);
                    Logger.Info($"上月总结生成结果：{(result != null ? "成功" : "null")}");
                    if (result != null)
                        AISummaryRepository.Insert(lastMonthStart, result, "monthly", "auto");
                }
                else
                {
                    AISummaryRepository.Insert(lastMonthStart, "本月没有活动记录。", "monthly", "auto");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("自动周/月总结检查失败", ex);
        }
    }


    /// <summary>
    /// 在底部状态栏显示临时提示信息，3.5 秒后自动清除
    /// </summary>
    private void ShowStatus(string message)
    {
        StatusBar.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (s, e) =>
        {
            StatusBar.Text = "";
            timer.Stop();
        };
        timer.Start();
    }

    // ========== 托盘图标 ==========

    /// <summary>
    /// 初始化系统托盘图标：注册 Windows 消息钩子，设置双击/右键/退出回调
    /// </summary>
    private void InitTray()
    {
        var hwndSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        _trayIcon = new TrayIcon(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            "TimeActivity");
        _trayIcon.OnDoubleClick = () => ShowFromTray();
        _trayIcon.OnShowMenu = () =>
        {
            _trayIcon.ShowContextMenuAtCursor(_engine.IsRunning);
        };
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
    /// Windows 消息处理：接收托盘图标的消息（点击、双击等）
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

    /// <summary>
    /// 从托盘恢复窗口：显示并激活
    /// </summary>
    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 关闭按钮处理：默认最小化到托盘，只有 _forceClose=true 时才真正退出
    /// </summary>
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

        // 真正退出
        _engine.Stop();
        _screenshotService.Stop();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

    // ========== 分类颜色 ==========

    /// <summary>
    /// 从数据库加载分类颜色到缓存
    /// </summary>
    private void LoadCategoryColors()
    {
        _categoryColors = _colorHelper.Load();
    }

    /// <summary>
    /// 从缓存获取分类颜色
    /// </summary>
    private Color GetCategoryColor(string category)
    {
        return _colorHelper.GetColor(category);
    }

    // ========== 宽度计算 ==========

    /// <summary>获取时间轴容器的实际可用宽度（减去 Padding）</summary>
    private double GetContainerWidth()
    {
        double w = TimelineContainer.ActualWidth - 16; // 减去 Padding
        if (w <= 0) w = 880;
        return w;
    }

    // ========== 按钮事件 ==========

    /// <summary>开始追踪按钮</summary>
    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        _engine.Start();
        if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
            _screenshotService.Start();
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        StatusText.Text = "追踪中...";
    }

    /// <summary>停止追踪按钮</summary>
    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _screenshotService.Stop();
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        StatusText.Text = "已停止";
    }

    /// <summary>刷新按钮：重新加载当天数据</summary>
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadDateData(_currentDate, isDateChange: true);

    /// <summary>上一天按钮</summary>
    private void BtnPrevDay_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = _currentDate.AddDays(-1);
        LoadDateData(_currentDate, isDateChange: true);
    }

    /// <summary>下一天按钮（不能超过今天）</summary>
    private void BtnNextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDate >= DateTime.Today) return;
        _currentDate = _currentDate.AddDays(1);
        LoadDateData(_currentDate, isDateChange: true);
    }

    /// <summary>跳回今天按钮</summary>
    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = DateTime.Today;
        LoadDateData(_currentDate, isDateChange: true);
    }

    // ========== 追踪回调 ==========

    /// <summary>
    /// 追踪引擎状态变化回调：更新状态栏显示当前活动窗口和分类
    /// </summary>
    private void OnStatusChanged(string process, string title, string category)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"{process} — {title}";
            CategoryText.Text = category;
        });
    }

    /// <summary>
    /// 追踪引擎记录到新活动回调：插入到列表顶部并触发防抖刷新
    /// </summary>
    private void OnActivityRecorded(ActivityRecord activity)
    {
        // Dispatcher.BeginInvoke: 切回 UI 线程更新界面
        Dispatcher.BeginInvoke(() =>
        {
            if (_currentDate == DateTime.Today)
            {
                _items.Insert(0, CreateDisplayItem(activity));
                while (_items.Count > 500)
                    _items.RemoveAt(_items.Count - 1);
            }
            ScheduleDebounceRefresh();
        });
    }

    /// <summary>
    /// 防抖刷新：500ms 内多次触发只执行最后一次，避免频繁查库重绘
    /// </summary>
    private void ScheduleDebounceRefresh()
    {
        if (_debounceTimer == null)
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMs)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer!.Stop();
                if (_currentDate == DateTime.Today)
                {
                    _cachedActivities = ActivityRepository.GetByDate(DateTime.Today);
                    DrawAll();
                    UpdateTodayTotal();
                }
            };
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // ========== 数据加载 ==========

    /// <summary>
    /// 加载指定日期的活动数据：更新日期文字、填充列表、重绘时间轴
    /// </summary>
    /// <param name="date">要加载的日期</param>
    /// <param name="isDateChange">是否是切换日期（true=清空勾选+重建统计列表）</param>
    private void LoadDateData(DateTime date, bool isDateChange = false)
    {
        // 设置日期显示文字
        if (date == DateTime.Today)
            DateText.Text = "今天";
        else if (date == DateTime.Today.AddDays(-1))
            DateText.Text = "昨天";
        else
            DateText.Text = date.ToString("MM-dd");

        // 下一天按钮在查看今天时禁用
        BtnNextDay.IsEnabled = date < DateTime.Today;

        // 清空旧列表，倒序填充（最新的在最上面）
        _items.Clear();
        var activities = ActivityRepository.GetByDate(date);
        _cachedActivities = activities;
        foreach (var a in activities.AsEnumerable().Reverse())
        {
            _items.Add(CreateDisplayItem(a));
        }

        // 切日期时清空勾选 + 重建统计列表（自动刷新不动勾选）
        if (isDateChange)
        {
            _checkedApps.Clear();
            _checkedCategories.Clear();
            if (StatsListPanel?.Visibility == Visibility.Visible)
                LoadStatsLists();
        }

        DrawAll();
        UpdateTodayTotal();
    }

    /// <summary>
    /// 列表模式切换：使用明细 / 使用占比
    /// </summary>
    private void RbListMode_Checked(object sender, RoutedEventArgs e)
    {
        if (ActivityList == null || StatsListPanel == null) return;

        var rb = sender as RadioButton;
        if (rb?.Tag?.ToString() == "stats")
        {
            ActivityList.Visibility = Visibility.Collapsed;
            StatsListPanel.Visibility = Visibility.Visible;
            LoadStatsLists();
        }
        else
        {
            ActivityList.Visibility = Visibility.Visible;
            StatsListPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ========== 使用占比列表 ==========

    /// <summary>
    /// 加载使用占比统计：按应用和按分类两个列表，各自计算时长和百分比
    /// </summary>
    private void LoadStatsLists()
    {
        // 清空两个列表
        AppStatsList.Items.Clear();
        CategoryStatsList.Items.Clear();

        // 从缓存数据聚合（排除空闲时段）
        var activities = _cachedActivities.Where(a => !a.IsIdle).ToList();
        if (activities.Count == 0) return;

        // 总活跃秒数
        int totalSeconds = activities.Sum(a => a.Duration);

        // 应用统计：按进程名分组，按时长降序
        var appGroups = activities
            .GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToList();

        foreach (var g in appGroups)
        {
            int sec = g.Sum(a => a.Duration);
            double pct = totalSeconds > 0 ? sec * 100.0 / totalSeconds : 0;
            string cat = g.First().Category;
            var row = CreateStatsRow(false, g.Key, cat, sec, pct,
                AppColorAllocator.GetOrAssign(g.Key),
                IconExtractor.GetIcon(g.Key),
                Services.AppDisplayName.Get(g.Key));
            AppStatsList.Items.Add(row);
        }

        // 类别统计：按分类名分组，按时长降序
        var catGroups = activities
            .GroupBy(a => a.Category)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToList();

        foreach (var g in catGroups)
        {
            int sec = g.Sum(a => a.Duration);
            double pct = totalSeconds > 0 ? sec * 100.0 / totalSeconds : 0;
            string color = _categoryColors.TryGetValue(g.Key, out var c) ? c : "#7F8C8D";
            var row = CreateStatsRow(true, g.Key, "", sec, pct, color, null, g.Key);
            CategoryStatsList.Items.Add(row);
        }
    }

    /// <summary>
    /// 创建一行统计行 UI（复杂方法）：复选框 + 图标 + 名称 + 类别 + 占比条 + 时长
    /// </summary>
    /// <param name="isCategory">true=类别统计行，false=应用统计行</param>
    /// <param name="name">进程名或类别名</param>
    /// <param name="category">分类名（应用行用）</param>
    /// <param name="seconds">总时长秒数</param>
    /// <param name="pct">百分比 0~100</param>
    /// <param name="barColor">占比条颜色的十六进制</param>
    /// <param name="icon">应用图标（类别行为 null）</param>
    /// <param name="displayName">显示名（友好名）</param>
    /// <returns>构建好的 Border 行</returns>
    private Border CreateStatsRow(bool isCategory, string name, string category, int seconds, double pct, string barColor, ImageSource? icon, string displayName)
    {
        var row = new Border { Padding = new Thickness(2), Margin = new Thickness(0, 1, 0, 1), Tag = name, Background = System.Windows.Media.Brushes.Transparent };
        // 用 Grid 布局一行：复选框 + 图标 + 名称 + 类别 + 占比条 + 时长
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });   // checkbox
        if (!isCategory)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });   // icon
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(isCategory ? 100 : 80) }); // name
        if (!isCategory)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });   // category
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // bar
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // duration

        int col = 0;

        // 复选框（勾选后高亮对应时间轴色块）
        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Tag = name };
        if (isCategory)
        {
            cb.Checked += CatStatsRow_CheckChanged;
            cb.Unchecked += CatStatsRow_CheckChanged;
        }
        else
        {
            cb.Checked += AppStatsRow_CheckChanged;
            cb.Unchecked += AppStatsRow_CheckChanged;
        }
        Grid.SetColumn(cb, col++);
        grid.Children.Add(cb);

        // 图标（仅应用行有）
        if (!isCategory)
        {
            var img = new Image { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(img, col++);
            grid.Children.Add(img);
        }

        // 名称
        var nameTb = new TextBlock { Text = displayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(nameTb, col++);
        grid.Children.Add(nameTb);

        // 类别（仅应用行有）
        if (!isCategory)
        {
            var catTb = new TextBlock { Text = category, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11 };
            Grid.SetColumn(catTb, col++);
            grid.Children.Add(catTb);
        }

        // 占比条 — 用 Canvas 实现，固定宽度 120px
        const double BarWidth = 120;
        const double BarHeight = 14;
        var barCanvas = new Canvas { Width = BarWidth, Height = BarHeight, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

        // 外框（灰色边框）
        var barBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), BorderThickness = new Thickness(1), Height = BarHeight, CornerRadius = new CornerRadius(2) };
        Canvas.SetLeft(barBorder, 0); Canvas.SetTop(barBorder, 0);
        barCanvas.Children.Add(barBorder);

        // 有色部分（按百分比填充）
        double fillWidth = BarWidth * pct / 100.0;
        var fillColor = CategoryColorHelper.ParseHex(barColor);
        var fillBorder = new Border { Background = new SolidColorBrush(fillColor), Height = BarHeight - 2, CornerRadius = new CornerRadius(2, 0, 0, 2) };
        Canvas.SetLeft(fillBorder, 1); Canvas.SetTop(fillBorder, 1);
        fillBorder.Width = Math.Max(0, fillWidth - 1);
        barCanvas.Children.Add(fillBorder);

        // 百分比文字：>80% 放在有色部分上（白字），否则放在透明部分（黑字）
        string pctText = $"{pct:F1}%";
        var pctTb = new TextBlock { Text = pctText, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        pctTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = pctTb.DesiredSize.Width;

        if (pct > 80)
        {
            // 放在有色部分上居中，白色字
            pctTb.Foreground = Brushes.White;
            Canvas.SetLeft(pctTb, Math.Max(1, (fillWidth - textWidth) / 2));
        }
        else
        {
            // 放在透明部分开头，黑色字
            pctTb.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            Canvas.SetLeft(pctTb, fillWidth + 2);
        }
        Canvas.SetTop(pctTb, (BarHeight - pctTb.DesiredSize.Height) / 2);
        barCanvas.Children.Add(pctTb);

        Grid.SetColumn(barCanvas, col++);
        grid.Children.Add(barCanvas);

        // 时长
        var durTb = new TextBlock { Text = TimeFormatHelper.Format(seconds), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(durTb, col++);
        grid.Children.Add(durTb);

        row.Child = grid;
        return row;
    }

    /// <summary>
    /// 应用统计行复选框变化：添加/移除高亮集合并重绘时间轴
    /// </summary>
    private void AppStatsRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string appName)
        {
            if (cb.IsChecked == true)
                _checkedApps.Add(appName);
            else
                _checkedApps.Remove(appName);
            DrawAll();
        }
    }

    /// <summary>
    /// 类别统计行复选框变化：添加/移除高亮集合并重绘时间轴
    /// </summary>
    private void CatStatsRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string catName)
        {
            if (cb.IsChecked == true)
                _checkedCategories.Add(catName);
            else
                _checkedCategories.Remove(catName);
            DrawAll();
        }
    }

    /// <summary>
    /// 更新今日（或选中日期）活跃总时长显示
    /// </summary>
    private void UpdateTodayTotal()
    {
        var summary = ActivityRepository.GetCategorySummaryByDate(_currentDate);
        int totalSeconds = summary.Values.Sum();
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        string label = _currentDate == DateTime.Today ? "今日活跃" : $"{_currentDate:MM-dd} 活跃";
        TodayTotalText.Text = $"{label}：{ts.Hours}h{ts.Minutes}m";
    }

    // ========== 绘制：统一入口 ==========

    /// <summary>
    /// 统一绘制入口：画时间轴色块、刻度、概览条、图例，更新缩放显示
    /// </summary>
    private void DrawAll()
    {
        double w = GetContainerWidth();

        // 上方时间轴
        TopScaleCanvas.Width = w;
        MainTimelineCanvas.Width = w;
        _timelineRenderer.DrawActivities(MainTimelineCanvas, w, TimelineHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds,
            ActiveAppHighlights.Count > 0 ? ActiveAppHighlights : null,
            ActiveCategoryHighlights.Count > 0 ? ActiveCategoryHighlights : null);
        _timelineRenderer.DrawScale(TopScaleCanvas, w, _viewStartSeconds, _visibleSeconds);

        // 下方概览条
        OverviewCanvas.Width = w;
        OverviewScaleCanvas.Width = w;
        _overviewRenderer.Draw(OverviewCanvas, w, OverviewHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds);
        _overviewRenderer.DrawScale(OverviewScaleCanvas, w);

        // 更新缩放显示
        double zoomLevel = 86400.0 / _visibleSeconds;
        ZoomText.Text = $"缩放：{zoomLevel:F1}x";
    }

    // ========== 滚轮缩放（跟随鼠标） ==========

    /// <summary>
    /// 鼠标滚轮缩放时间轴：以鼠标位置为缩放中心，保持鼠标处的时间不变
    /// </summary>
    private void MainTimelineCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 鼠标在时间轴上的 X 坐标
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        // 鼠标 X 坐标对应的时间（秒）
        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 滚轮上滚放大（×0.8），下滚缩小（×1.25）
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        double newVisible = Math.Clamp(_visibleSeconds * factor, MinVisibleSeconds, MaxVisibleSeconds);
        if (newVisible == _visibleSeconds) { e.Handled = true; return; }

        _visibleSeconds = newVisible;

        // 调整起始位置使鼠标处的时间保持不变
        _viewStartSeconds = mouseTime - (mouseX / width) * _visibleSeconds;
        _viewStartSeconds = Math.Clamp(_viewStartSeconds, 0, MaxVisibleSeconds - _visibleSeconds);

        DrawAll();
        e.Handled = true;
    }

    // ========== 概览条拖拽（平移可见范围） ==========

    /// <summary>鼠标按下开始拖拽：记录起点</summary>
    private void OverviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = true;
        _dragStartX = e.GetPosition(OverviewCanvas).X;
        _dragStartViewStart = _viewStartSeconds;
        OverviewCanvas.CaptureMouse();
    }

    /// <summary>鼠标移动拖拽中：按偏移量平移可见范围</summary>
    private void OverviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_overviewDragging) return;
        double width = GetContainerWidth();
        double curX = e.GetPosition(OverviewCanvas).X;
        double deltaSeconds = ((curX - _dragStartX) / width) * 86400;
        _viewStartSeconds = Math.Clamp(_dragStartViewStart + deltaSeconds, 0, MaxVisibleSeconds - _visibleSeconds);
        DrawAll();
    }

    /// <summary>鼠标释放结束拖拽</summary>
    private void OverviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = false;
        OverviewCanvas.ReleaseMouseCapture();
    }

    // ========== 鼠标悬停浮动详情框 ==========

    /// <summary>
    /// 鼠标在时间轴上移动时：查找当前位置对应的活动，显示浮动 Popup 详情（含截图）
    /// </summary>
    private void MainTimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        // 鼠标 X 坐标对应的时间（秒）
        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 查找鼠标时间落在哪个活动区间
        ActivityRecord? hit = null;
        foreach (var act in _cachedActivities)
        {
            if (act.IsIdle) continue;
            // 用绝对时间比较，不用 TimeOfDay（跨午夜活动EndTime.TimeOfDay会归零）
            // 将活动起止转为当天秒数，跨午夜活动endSec会小于startSec，需特殊处理
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double endSec = act.EndTime.TimeOfDay.TotalSeconds;
            // 跨午夜活动（endSec < startSec），endSec 加一天秒数
            if (endSec < startSec) endSec += 86400;
            // mouseTime 也可能小于 startSec（如凌晨0点后看前一天23点开始的活踯）
            double mt = mouseTime;
            if (mt < startSec) mt += 86400;
            if (mt >= startSec && mt < endSec)
            {
                hit = act;
                break;
            }
        }

        if (!_popupOpen)
        {
            DetailPopup.IsOpen = true;
            _popupOpen = true;
        }

        // 每次移动都更新 Popup 位置 — 相对于 MainTimelineCanvas
        var canvasPos = e.GetPosition(MainTimelineCanvas);
        DetailPopup.HorizontalOffset = canvasPos.X + 14;
        DetailPopup.VerticalOffset = canvasPos.Y + 18;

        if (hit != null)
        {
            // 命中活动：显示颜色块、分类、时间、进程名、标题、截图
            PopupColor.Visibility = Visibility.Visible;
            PopupCategory.Visibility = Visibility.Visible;
            PopupProcess.Visibility = Visibility.Visible;
            PopupColor.Fill = new SolidColorBrush(GetCategoryColor(hit.Category));
            PopupCategory.Text = $"{hit.Category}  ·  {TimeFormatHelper.Format(hit.Duration)}";
            PopupTime.Text = $"{hit.StartTime:HH:mm:ss} → {hit.EndTime:HH:mm:ss}";
            PopupProcess.Text = hit.ProcessName;
            PopupTitle.Text = hit.WindowTitle;
            PopupTitle.Visibility = string.IsNullOrEmpty(hit.WindowTitle) ? Visibility.Collapsed : Visibility.Visible;

            // 用活动的开始~结束时间查截图（截图必须在活动期间内拍的）
            var screenshotPath = ScreenshotService.GetScreenshotForTime(hit.StartTime, hit.EndTime);
            if (screenshotPath != null)
            {
                if (_lastScreenshotPath != screenshotPath)
                {
                    PopupScreenshot.Source = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(screenshotPath));
                    _lastScreenshotPath = screenshotPath;
                }
                PopupScreenshot.Visibility = Visibility.Visible;
            }
            else
            {
                // 没有截图时清空缓存和 Source，防止残留旧图
                _lastScreenshotPath = null;
                PopupScreenshot.Source = null;
                PopupScreenshot.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            // 没命中活动时只显示时间1
            TimeSpan ts = TimeSpan.FromSeconds(mouseTime);
            PopupColor.Visibility = Visibility.Collapsed;
            PopupCategory.Visibility = Visibility.Collapsed;
            PopupProcess.Visibility = Visibility.Collapsed;
            PopupTitle.Visibility = Visibility.Collapsed;
            PopupScreenshot.Visibility = Visibility.Collapsed;
            PopupTime.Text = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    /// <summary>
    /// 鼠标离开时间轴：关闭浮动详情框，清空截图缓存
    /// </summary>
    private void MainTimelineCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        DetailPopup.IsOpen = false;
        _popupOpen = false;
        _lastScreenshotPath = null;
        PopupScreenshot.Source = null;
        PopupScreenshot.Visibility = Visibility.Collapsed;
    }

    // ========== 图例 ==========

    /// <summary>
    /// 绘制分类颜色图例：每个分类一个色块 + 名称
    /// </summary>
    private void DrawLegend()
    {
        LegendPanel.Children.Clear();
        foreach (var kvp in _categoryColors)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            var rect = new Rectangle
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(CategoryColorHelper.ParseHex(kvp.Value)),
                RadiusX = 2, RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            var text = new TextBlock
            {
                Text = kvp.Key, FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(rect);
            panel.Children.Add(text);
            LegendPanel.Children.Add(panel);
        }
    }

}

