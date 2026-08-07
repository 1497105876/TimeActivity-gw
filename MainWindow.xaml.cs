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

public partial class MainWindow : Window
{
    private readonly TrackingEngine _engine;
    private readonly ActivityClassifier _classifier;
    private readonly ScreenshotService _screenshotService;
    private readonly ObservableCollection<ActivityDisplayItem> _items = new();
    private StatisticsPage? _statsPage;

    private readonly CategoryColorHelper _colorHelper = new();
    private readonly TimelineRenderer _timelineRenderer;
    private readonly OverviewRenderer _overviewRenderer;
    private Dictionary<string, string> _categoryColors = new();
    private DateTime _currentDate = DateTime.Today;

    // === 时间轴核心参数 ===
    // 可见时间范围（秒），1x 时 = 86400（全天）
    // 滚轮缩放改这个值，越小越放大
    private double _visibleSeconds = 86400;
    private const double MinVisibleSeconds = 300; // 最小5分钟
    private const double MaxVisibleSeconds = 86400; // 最大24小时

    // 可见范围起始时间（秒，0~86400-visibleSeconds）
    private double _viewStartSeconds = 0;

    private const int TimelineHeight = 44;
    private const int OverviewHeight = 20;

    // 防抖
    private System.Windows.Threading.DispatcherTimer? _debounceTimer;
    private const int DebounceMs = 500;

    // 自动刷新
    private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;

    // 缓存
    private List<ActivityRecord> _cachedActivities = new();

    // 自动周/月总结检查日期缓存（同一天只查一次）
    private DateTime _lastAutoSummaryCheckDate = DateTime.MinValue;

    // Popup 标志
    private bool _popupOpen = false;
    private string? _lastScreenshotPath = null;

    // 概览条拖拽
    private bool _overviewDragging = false;
    private double _dragStartX = 0;
    private double _dragStartViewStart = 0;

    // 托盘
    private TrayIcon? _trayIcon;
    private bool _forceClose = false;

    // === 使用占比高亮 ===
    private readonly HashSet<string> _checkedApps = new();          // 勾选高亮的应用名
    private readonly HashSet<string> _checkedCategories = new();    // 勾选高亮的类别名
    private readonly Dictionary<string, string> _appBarColors = new(); // 应用名→占比条颜色缓存
    private readonly Random _colorRandom = new();

    // 勾选高亮集合（唯一高亮来源）
    private HashSet<string> ActiveAppHighlights => _checkedApps;
    private HashSet<string> ActiveCategoryHighlights => _checkedCategories;

    public MainWindow()
    {
        InitializeComponent();

        DatabaseHelper.Initialize();
        LoadCategoryColors();

        _timelineRenderer = new TimelineRenderer(_colorHelper);
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        _overviewRenderer = new OverviewRenderer(_colorHelper);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);

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

        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = poll;
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = idle;

        _engine.OnActivityRecorded += OnActivityRecorded;
        _engine.OnStatusChanged += OnStatusChanged;

        ActivityList.ItemsSource = _items;

        // 加载颜色模式
        _colorMode = SettingsRepository.Get("ColorMode", "category");
        if (_colorMode == "app")
        {
            RbColorCategory.IsChecked = false;
            RbColorApp.IsChecked = true;
        }
        AppColorAllocator.LoadFromDb();

        DrawLegend();
        LoadDateData(_currentDate, isDateChange: true);

        // 统计页
        _statsPage = new StatisticsPage();
        StatsFrame.Navigate(_statsPage);

        // 设置页改为独立窗口，不再在 Tab 里加载

        // 窗口大小变化时重绘 — 整体等比缩放
        TimelineContainer.SizeChanged += (s, e) =>
        {
            if (e.WidthChanged)
                DrawAll();
        };

        // 自动刷新
        _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _autoRefreshTimer.Tick += (s, e) =>
        {
            if (_currentDate == DateTime.Today)
            {
                // 轻量刷新：只查库+重绘时间轴，不重建 ListView（避免卡顿）
                var activities = ActivityRepository.GetByDate(_currentDate);
                _cachedActivities = activities;

                // 追加新记录到列表（只加末尾新增的）
                int existingCount = _items.Count;
                for (int i = existingCount; i < activities.Count; i++)
                {
                    var a = activities[i];
                    _items.Add(CreateDisplayItem(a));
                }
                // 更新已有记录的结束时间和时长（最后一条可能还在进行中）
                if (_items.Count > 0 && activities.Count > 0)
                {
                    var last = activities[activities.Count - 1];
                    var item = _items[_items.Count - 1];
                    item.EndTime = last.EndTime;
                    item.DurationText = TimeFormatHelper.Format(last.Duration);
                }

                DrawAll();
                UpdateTodayTotal();
            }
            _ = CheckAutoSummaryAsync();

            // 每次自动刷新都更新当天的汇总（一天的数据量不大，GROUP BY 很快）
            try { DailySummaryRepository.GenerateForDate(DateTime.Today.ToString("yyyy-MM-dd")); }
            catch { }
        };
        _autoRefreshTimer.Start();

        // 启动时执行数据保留清理
        PerformDataRetention();

        // 启动时检查是否需要补生成上周/上月的自动总结
        _ = CheckAutoSummaryAsync();

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

    private void OnSettingsSaved()
    {
        // 截图服务：如果在跑就重启（重新读设置）
        if (_screenshotService.IsRunning)
        {
            _screenshotService.Stop();
            if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                _screenshotService.Start();
        }
        else
        {
            if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
                _screenshotService.Start();
        }

        // 追踪引擎重读采样间隔和空闲阈值
        if (int.TryParse(SettingsRepository.Get("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = poll;
        if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = idle;

        // 分类器重载规则 + 重新分类历史数据
        _classifier.ReloadRules();
        try { DatabaseHelper.ReclassifyAll(_classifier.Classify); } catch { }

        // 重载分类颜色（用户可能改了分类颜色）
        LoadCategoryColors();
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        DrawLegend();
        DrawAll();

        // 执行数据保留清理
        PerformDataRetention();
    }

    // ========== 设置窗口 + 颜色模式 + 右键菜单 ==========

    private string _colorMode = "category"; // "category" or "app"

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
    /// 根据当前颜色模式获取某个应用的颜色
    /// </summary>
    private Color GetAppColor(string processName, string category)
    {
        if (_colorMode == "app")
        {
            var hex = AppColorAllocator.GetOrAssign(processName);
            return CategoryColorHelper.ParseHex(hex);
        }
        return _colorHelper.GetColor(category);
    }

    // ========== 右键菜单 ==========

    private void AppStatsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(AppStatsList);

            // 找点击的行
            var item = GetListViewItemFromPoint(AppStatsList, pos);
            if (item == null) return;

            // 从行中提取进程名
            string? processName = GetTagFromStatsRow(item);
            if (string.IsNullOrEmpty(processName)) return;

            var menu = new ContextMenu();

            var miColor = new MenuItem { Header = "颜色" };
            miColor.Click += (s, ev) =>
            {
                try
                {
                    var dlg = new System.Windows.Forms.ColorDialog();
                    dlg.FullOpen = true;
                    var current = AppColorAllocator.GetOrAssign(processName);
                    try
                    {
                        var c = CategoryColorHelper.ParseHex(current);
                        dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                    }
                    catch { }
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                        AppColorAllocator.SetCustom(processName, hex);
                        DrawAll();
                        LoadStatsLists();
                    }
                }
                catch (Exception ex) { MessageBox.Show($"颜色选择失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            menu.Items.Add(miColor);

            var miCategory = new MenuItem { Header = "更改类别" };
            miCategory.Click += (s, ev) => OpenSettings("rules");
            menu.Items.Add(miCategory);

            menu.IsOpen = true;
        }
        catch (Exception ex) { MessageBox.Show($"右键菜单失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

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
            miColor.Click += (s, ev) => OpenSettings("categories");
            menu.Items.Add(miColor);

            var miView = new MenuItem { Header = "查看类别" };
            miView.Click += (s, ev) => OpenSettings("rules");
            menu.Items.Add(miView);

            menu.IsOpen = true;
        }
        catch (Exception ex) { MessageBox.Show($"右键菜单失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ========== 右键菜单辅助方法 ==========

    private static object? GetListViewItemFromPoint(System.Windows.Controls.ListView list, System.Windows.Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.ListViewItem)
            hit = VisualTreeHelper.GetParent(hit);
        return hit;
    }

    private static ActivityDisplayItem CreateDisplayItem(ActivityRecord a)
    {
        return new ActivityDisplayItem
        {
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
    /// 检查是否需要自动生成周/月总结（启动兆底 + 定时检查）
    /// </summary>
    private async Task CheckAutoSummaryAsync()
    {
        // 同一天只执行一次，避免 30 秒刷新重复查库
        if (_lastAutoSummaryCheckDate == DateTime.Today) return;
        _lastAutoSummaryCheckDate = DateTime.Today;

        try
        {
            if (SettingsRepository.Get("EnableAI", "true") != "true") return;

            var aiService = new AISummaryService();
            DateTime today = DateTime.Today;

            // 检查上周总结
            DateTime lastWeekStart = DateHelper.GetWeekStart(today.AddDays(-7));
            if (!AISummaryRepository.HasAuto(lastWeekStart, "weekly"))
            {
                int weekSeconds = ActivityRepository.GetCategorySummaryByRange(lastWeekStart, lastWeekStart.AddDays(6), false).Values.Sum();
                if (weekSeconds > 0)
                {
                    Logger.Info($"补生成上周总结：{lastWeekStart:yyyy-MM-dd}");
                    string? result = await aiService.GenerateWeeklySummary(lastWeekStart);
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
            if (!AISummaryRepository.HasAuto(lastMonthStart, "monthly"))
            {
                int monthSeconds = ActivityRepository.GetCategorySummaryByRange(lastMonthStart, lastMonthStart.AddMonths(1).AddDays(-1), false).Values.Sum();
                if (monthSeconds > 0)
                {
                    Logger.Info($"补生成上月总结：{lastMonthStart:yyyy-MM-dd}");
                    string? result = await aiService.GenerateMonthlySummary(lastMonthStart);
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

    // ========== 托盘 ==========

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayIcon.WM_TRAYICON)
        {
            _trayIcon?.HandleMessage(wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
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

        // 真正退出
        _engine.Stop();
        _screenshotService.Stop();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

    // ========== 分类颜色 ==========

    private void LoadCategoryColors()
    {
        _categoryColors = _colorHelper.Load();
    }

    private Color GetCategoryColor(string category)
    {
        return _colorHelper.GetColor(category);
    }

    // ========== 宽度计算 ==========

    /// <summary>容器实际可用宽度</summary>
    private double GetContainerWidth()
    {
        double w = TimelineContainer.ActualWidth - 16; // 减去 Padding
        if (w <= 0) w = 880;
        return w;
    }

    // ========== 按钮事件 ==========

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        _engine.Start();
        if (SettingsRepository.Get("EnableScreenshot", "false") == "true")
            _screenshotService.Start();
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        StatusText.Text = "追踪中...";
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _screenshotService.Stop();
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        StatusText.Text = "已停止";
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadDateData(_currentDate, isDateChange: true);

    private void BtnPrevDay_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = _currentDate.AddDays(-1);
        LoadDateData(_currentDate, isDateChange: true);
    }

    private void BtnNextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDate >= DateTime.Today) return;
        _currentDate = _currentDate.AddDays(1);
        LoadDateData(_currentDate, isDateChange: true);
    }

    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = DateTime.Today;
        LoadDateData(_currentDate, isDateChange: true);
    }

    // ========== 追踪回调 ==========

    private void OnStatusChanged(string process, string title, string category)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"{process} — {title}";
            CategoryText.Text = category;
        });
    }

    private void OnActivityRecorded(ActivityRecord activity)
    {
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

    private void ScheduleDebounceRefresh()
    {
        _debounceTimer?.Stop();
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
        _debounceTimer.Start();
    }

    // ========== 数据加载 ==========

    private void LoadDateData(DateTime date, bool isDateChange = false)
    {
        if (date == DateTime.Today)
            DateText.Text = "今天";
        else if (date == DateTime.Today.AddDays(-1))
            DateText.Text = "昨天";
        else
            DateText.Text = date.ToString("MM-dd");

        BtnNextDay.IsEnabled = date < DateTime.Today;

        _items.Clear();
        var activities = ActivityRepository.GetByDate(date);
        _cachedActivities = activities;
        foreach (var a in activities.AsEnumerable().Reverse())
        {
            _items.Add(CreateDisplayItem(a));
        }

        // 仅切日期时清空勾选 + 重建统计列表（自动刷新不动勾选）
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
    /// 切换列表显示模式：使用明细 / 使用占比
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

    private string GetRandomBarColor()
    {
        var colors = new[] { "#4A90D9", "#E67E22", "#E74C3C", "#2ECC71", "#9B59B6", "#1ABC9C", "#F39C12", "#E91E63", "#00BCD4", "#8BC34A", "#FF5722", "#3F51B5" };
        return colors[_colorRandom.Next(colors.Length)];
    }

    private string GetAppBarColor(string appName)
    {
        if (!_appBarColors.TryGetValue(appName, out var color))
        {
            color = GetRandomBarColor();
            _appBarColors[appName] = color;
        }
        return color;
    }

    private void LoadStatsLists()
    {
        AppStatsList.Items.Clear();
        CategoryStatsList.Items.Clear();

        // 从缓存的活动数据聚合
        var activities = _cachedActivities.Where(a => !a.IsIdle).ToList();
        if (activities.Count == 0) return;

        int totalSeconds = activities.Sum(a => a.Duration);

        // 应用统计
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

        // 类别统计
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

    private Border CreateStatsRow(bool isCategory, string name, string category, int seconds, double pct, string barColor, ImageSource? icon, string displayName)
    {
        var row = new Border { Padding = new Thickness(2), Margin = new Thickness(0, 1, 0, 1), Tag = name, Background = System.Windows.Media.Brushes.Transparent };
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

        // Checkbox
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

        // Icon (apps only)
        if (!isCategory)
        {
            var img = new Image { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(img, col++);
            grid.Children.Add(img);
        }

        // Name
        var nameTb = new TextBlock { Text = displayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(nameTb, col++);
        grid.Children.Add(nameTb);

        // Category (apps only)
        if (!isCategory)
        {
            var catTb = new TextBlock { Text = category, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11 };
            Grid.SetColumn(catTb, col++);
            grid.Children.Add(catTb);
        }

        // Bar — Canvas 实现，固定宽度 120
        const double BarWidth = 120;
        const double BarHeight = 14;
        var barCanvas = new Canvas { Width = BarWidth, Height = BarHeight, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

        // 外框
        var barBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), BorderThickness = new Thickness(1), Height = BarHeight, CornerRadius = new CornerRadius(2) };
        Canvas.SetLeft(barBorder, 0); Canvas.SetTop(barBorder, 0);
        barCanvas.Children.Add(barBorder);

        // 有色部分
        double fillWidth = BarWidth * pct / 100.0;
        var fillColor = CategoryColorHelper.ParseHex(barColor);
        var fillBorder = new Border { Background = new SolidColorBrush(fillColor), Height = BarHeight - 2, CornerRadius = new CornerRadius(2, 0, 0, 2) };
        Canvas.SetLeft(fillBorder, 1); Canvas.SetTop(fillBorder, 1);
        fillBorder.Width = Math.Max(0, fillWidth - 1);
        barCanvas.Children.Add(fillBorder);

        // 百分比文字
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

        // Duration
        var durTb = new TextBlock { Text = TimeFormatHelper.Format(seconds), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(durTb, col++);
        grid.Children.Add(durTb);

        row.Child = grid;
        return row;
    }

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

    private void UpdateTodayTotal()
    {
        var summary = ActivityRepository.GetCategorySummaryByDate(_currentDate);
        int totalSeconds = summary.Values.Sum();
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        string label = _currentDate == DateTime.Today ? "今日活跃" : $"{_currentDate:MM-dd} 活跃";
        TodayTotalText.Text = $"{label}：{ts.Hours}h{ts.Minutes}m";
    }

    // ========== 绘制：统一入口 ==========

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

    private void MainTimelineCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 鼠标在时间轴上的 X 坐标
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        // 鼠标对应的时间（秒）
        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 缩放
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        double newVisible = Math.Clamp(_visibleSeconds * factor, MinVisibleSeconds, MaxVisibleSeconds);
        if (newVisible == _visibleSeconds) { e.Handled = true; return; }

        _visibleSeconds = newVisible;

        // 调整起始位置使鼠标时间不变
        _viewStartSeconds = mouseTime - (mouseX / width) * _visibleSeconds;
        _viewStartSeconds = Math.Clamp(_viewStartSeconds, 0, MaxVisibleSeconds - _visibleSeconds);

        DrawAll();
        e.Handled = true;
    }

    // ========== 概览条拖拽（平移可见范围） ==========

    private void OverviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = true;
        _dragStartX = e.GetPosition(OverviewCanvas).X;
        _dragStartViewStart = _viewStartSeconds;
        OverviewCanvas.CaptureMouse();
    }

    private void OverviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_overviewDragging) return;
        double width = GetContainerWidth();
        double curX = e.GetPosition(OverviewCanvas).X;
        double deltaSeconds = ((curX - _dragStartX) / width) * 86400;
        _viewStartSeconds = Math.Clamp(_dragStartViewStart + deltaSeconds, 0, MaxVisibleSeconds - _visibleSeconds);
        DrawAll();
    }

    private void OverviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = false;
        OverviewCanvas.ReleaseMouseCapture();
    }

    // ========== 鼠标悬停浮动详情框 ==========

    private void MainTimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 查找鼠标时间落在哪个活动区间
        ActivityRecord? hit = null;
        foreach (var act in _cachedActivities)
        {
            if (act.IsIdle) continue;
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double endSec = act.EndTime.TimeOfDay.TotalSeconds;
            if (mouseTime >= startSec && mouseTime < endSec)
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
            PopupColor.Visibility = Visibility.Visible;
            PopupCategory.Visibility = Visibility.Visible;
            PopupProcess.Visibility = Visibility.Visible;
            PopupColor.Fill = new SolidColorBrush(GetCategoryColor(hit.Category));
            PopupCategory.Text = $"{hit.Category}  ·  {TimeFormatHelper.Format(hit.Duration)}";
            PopupTime.Text = $"{hit.StartTime:HH:mm:ss} → {hit.EndTime:HH:mm:ss}";
            PopupProcess.Text = hit.ProcessName;
            PopupTitle.Text = hit.WindowTitle;
            PopupTitle.Visibility = string.IsNullOrEmpty(hit.WindowTitle) ? Visibility.Collapsed : Visibility.Visible;

            // 用鼠标当前时间点查截图（<= 该时间点最近的一张）
            DateTime mouseDateTime = _currentDate.Date.AddSeconds(mouseTime);
            var screenshotPath = ScreenshotService.GetScreenshotForTime(mouseDateTime);
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
                PopupScreenshot.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            TimeSpan ts = TimeSpan.FromSeconds(mouseTime);
            PopupColor.Visibility = Visibility.Collapsed;
            PopupCategory.Visibility = Visibility.Collapsed;
            PopupProcess.Visibility = Visibility.Collapsed;
            PopupTitle.Visibility = Visibility.Collapsed;
            PopupScreenshot.Visibility = Visibility.Collapsed;
            PopupTime.Text = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    private void MainTimelineCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        DetailPopup.IsOpen = false;
        _popupOpen = false;
    }

    // ========== 图例 ==========

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

