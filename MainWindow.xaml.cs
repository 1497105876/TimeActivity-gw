using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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

    // Popup 标志
    private bool _popupOpen = false;

    // 概览条拖拽
    private bool _overviewDragging = false;
    private double _dragStartX = 0;
    private double _dragStartViewStart = 0;

    // 托盘
    private TrayIcon? _trayIcon;
    private bool _forceClose = false;

    public MainWindow()
    {
        InitializeComponent();

        DatabaseHelper.Initialize();
        LoadCategoryColors();

        _timelineRenderer = new TimelineRenderer(_colorHelper);
        _overviewRenderer = new OverviewRenderer(_colorHelper);

        _classifier = new ActivityClassifier();
        _engine = new TrackingEngine(_classifier);
        _screenshotService = new ScreenshotService();

        if (int.TryParse(DatabaseHelper.GetSetting("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = poll;
        if (int.TryParse(DatabaseHelper.GetSetting("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = idle;

        _engine.OnActivityRecorded += OnActivityRecorded;
        _engine.OnStatusChanged += OnStatusChanged;

        ActivityList.ItemsSource = _items;

        DrawLegend();
        LoadDateData(_currentDate);

        // 统计页
        _statsPage = new StatisticsPage();
        StatsFrame.Navigate(_statsPage);

        var settingsPage = new SettingsPage();
        SettingsFrame.Navigate(settingsPage);

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
                LoadDateData(_currentDate);
        };
        _autoRefreshTimer.Start();

        // 启动时执行数据保留清理
        PerformDataRetention();

        if (DatabaseHelper.GetSetting("AutoStartTracking", "true") == "true")
        {
            _engine.Start();
            if (DatabaseHelper.GetSetting("EnableScreenshot", "false") == "true")
                _screenshotService.Start();
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            StatusText.Text = "追踪中...";
        }

        // 初始化托盘需等窗口句柄就绪
        this.SourceInitialized += (s, e) => InitTray();

        // 设置页保存后重启截图服务
        SettingsPage.SettingsSaved += OnSettingsSaved;

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
            if (DatabaseHelper.GetSetting("EnableScreenshot", "false") == "true")
                _screenshotService.Start();
        }
        else
        {
            if (DatabaseHelper.GetSetting("EnableScreenshot", "false") == "true")
                _screenshotService.Start();
        }

        // 追踪引擎重读采样间隔和空闲阈值
        if (int.TryParse(DatabaseHelper.GetSetting("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = poll;
        if (int.TryParse(DatabaseHelper.GetSetting("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = idle;

        // 分类器重载规则
        _classifier.ReloadRules();

        // 重载分类颜色（用户可能改了分类颜色）
        LoadCategoryColors();
        DrawLegend();
        DrawAll();

        // 执行数据保留清理
        PerformDataRetention();
    }

    /// <summary>
    /// 按设置的数据保留天数清理旧数据
    /// </summary>
    private void PerformDataRetention()
    {
        try
        {
            string? retentionStr = DatabaseHelper.GetSetting("DataRetentionDays", "90");
            if (int.TryParse(retentionStr, out int days) && days > 0)
            {
                int deleted = DatabaseHelper.CleanOldData(days);
                if (deleted > 0)
                    System.Diagnostics.Debug.WriteLine($"[DataRetention] 清理了 {deleted} 条超过 {days} 天的旧数据");
            }
        }
        catch { }
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
        if (!_forceClose && DatabaseHelper.GetSetting("MinimizeToTray", "true") == "true")
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
        if (DatabaseHelper.GetSetting("EnableScreenshot", "false") == "true")
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

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadDateData(_currentDate);

    private void BtnPrevDay_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = _currentDate.AddDays(-1);
        LoadDateData(_currentDate);
    }

    private void BtnNextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDate >= DateTime.Today) return;
        _currentDate = _currentDate.AddDays(1);
        LoadDateData(_currentDate);
    }

    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = DateTime.Today;
        LoadDateData(_currentDate);
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
                _items.Insert(0, new ActivityDisplayItem
                {
                    ProcessName = activity.ProcessName,
                    WindowTitle = activity.WindowTitle,
                    Category = activity.Category,
                    StartTime = activity.StartTime,
                    DurationText = TimelineRenderer.FormatDuration(activity.Duration)
                });
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
                _cachedActivities = DatabaseHelper.GetActivitiesByDate(DateTime.Today);
                DrawAll();
                UpdateTodayTotal();
            }
        };
        _debounceTimer.Start();
    }

    // ========== 数据加载 ==========

    private void LoadDateData(DateTime date)
    {
        if (date == DateTime.Today)
            DateText.Text = "今天";
        else if (date == DateTime.Today.AddDays(-1))
            DateText.Text = "昨天";
        else
            DateText.Text = date.ToString("MM-dd");

        BtnNextDay.IsEnabled = date < DateTime.Today;

        _items.Clear();
        var activities = DatabaseHelper.GetActivitiesByDate(date);
        _cachedActivities = activities;
        foreach (var a in activities.AsEnumerable().Reverse())
        {
            _items.Add(new ActivityDisplayItem
            {
                ProcessName = a.ProcessName,
                WindowTitle = a.WindowTitle,
                Category = a.Category,
                StartTime = a.StartTime,
                DurationText = TimelineRenderer.FormatDuration(a.Duration)
            });
        }

        DrawAll();
        UpdateTodayTotal();
    }

    private void UpdateTodayTotal()
    {
        var summary = DatabaseHelper.GetCategorySummaryByDate(_currentDate);
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
        _timelineRenderer.DrawActivities(MainTimelineCanvas, w, TimelineHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds);
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
            PopupCategory.Text = $"{hit.Category}  ·  {TimelineRenderer.FormatDuration(hit.Duration)}";
            PopupTime.Text = $"{hit.StartTime:HH:mm:ss} → {hit.EndTime:HH:mm:ss}";
            PopupProcess.Text = hit.ProcessName;
            PopupTitle.Text = hit.WindowTitle;
            PopupTitle.Visibility = string.IsNullOrEmpty(hit.WindowTitle) ? Visibility.Collapsed : Visibility.Visible;

            // 加载截图
            var screenshotPath = ScreenshotService.GetScreenshotForTime(hit.StartTime);
            if (screenshotPath != null)
            {
                PopupScreenshot.Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri(screenshotPath));
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
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kvp.Value)),
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

