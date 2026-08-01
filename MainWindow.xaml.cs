using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeActivity.Data;
using TimeActivity.Models;
using TimeActivity.Services;

namespace TimeActivity;

/// <summary>
/// 主窗口
/// </summary>
public partial class MainWindow : Window
{
    private readonly TrackingEngine _engine;
    private readonly ActivityClassifier _classifier;
    private readonly ObservableCollection<ActivityDisplayItem> _items = new();

    // 分类颜色缓存
    private static readonly Dictionary<string, string> CategoryColors = new()
    {
        { "开发", "#4A90D9" },
        { "社交", "#E67E22" },
        { "娱乐", "#E74C3C" },
        { "学习", "#2ECC71" },
        { "系统", "#95A5A6" },
        { "网页", "#9B59B6" },
        { "空闲", "#BDC3C7" },
        { "未分类", "#7F8C8D" },
    };

    public MainWindow()
    {
        InitializeComponent();

        // 初始化数据库（首次运行自动建库建表）
        DatabaseHelper.Initialize();

        _classifier = new ActivityClassifier();
        _engine = new TrackingEngine(_classifier);

        // 从数据库读取设置
        if (int.TryParse(DatabaseHelper.GetSetting("PollIntervalSeconds", "3"), out int poll))
            _engine.PollIntervalSeconds = poll;
        if (int.TryParse(DatabaseHelper.GetSetting("IdleThresholdSeconds", "300"), out int idle))
            _engine.IdleThresholdSeconds = idle;

        _engine.OnActivityRecorded += OnActivityRecorded;
        _engine.OnStatusChanged += OnStatusChanged;

        ActivityList.ItemsSource = _items;

        // 画图例
        DrawLegend();

        // 加载今天的活动列表和时间轴
        LoadTodayData();

        // 如果设置了自动开始追踪
        if (DatabaseHelper.GetSetting("AutoStartTracking", "true") == "true")
        {
            _engine.Start();
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            StatusText.Text = "追踪中...";
        }
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        _engine.Start();
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        StatusText.Text = "追踪中...";
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        StatusText.Text = "已停止";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadTodayData();
    }

    // 状态变化 — 在 UI 线程更新
    private void OnStatusChanged(string process, string title, string category)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"{process} — {title}";
            CategoryText.Text = category;
        });
    }

    // 新活动记录 — 加到列表里 + 刷新时间轴
    private void OnActivityRecorded(ActivityRecord activity)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _items.Insert(0, new ActivityDisplayItem
            {
                ProcessName = activity.ProcessName,
                WindowTitle = activity.WindowTitle,
                Category = activity.Category,
                StartTime = activity.StartTime,
                DurationText = FormatDuration(activity.Duration)
            });

            while (_items.Count > 500)
                _items.RemoveAt(_items.Count - 1);

            // 刷新时间轴和总计
            DrawTimeline();
            UpdateTodayTotal();
        });
    }

    /// <summary>
    /// 加载今天的活动数据到列表和时间轴
    /// </summary>
    private void LoadTodayData()
    {
        _items.Clear();

        var activities = DatabaseHelper.GetActivitiesByDate(DateTime.Today);
        // 倒序显示（最新的在上面）
        foreach (var a in activities.AsEnumerable().Reverse())
        {
            _items.Add(new ActivityDisplayItem
            {
                ProcessName = a.ProcessName,
                WindowTitle = a.WindowTitle,
                Category = a.Category,
                StartTime = a.StartTime,
                DurationText = FormatDuration(a.Duration)
            });
        }

        DrawTimeline();
        UpdateTodayTotal();
    }

    /// <summary>
    /// 更新今日总时长显示
    /// </summary>
    private void UpdateTodayTotal()
    {
        var summary = DatabaseHelper.GetCategorySummaryByDate(DateTime.Today);
        int totalSeconds = summary.Values.Sum();
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        TodayTotalText.Text = $"今日活跃：{ts.Hours}h{ts.Minutes}m";
    }

    /// <summary>
    /// 画 24 小时色块时间轴
    /// </summary>
    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();

        double width = TimelineCanvas.ActualWidth;
        if (width <= 0) width = 880; // 默认宽度
        double height = 36;

        // 背景
        var bg = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            RadiusX = 4,
            RadiusY = 4
        };
        Canvas.SetLeft(bg, 0);
        Canvas.SetTop(bg, 0);
        TimelineCanvas.Children.Add(bg);

        // 获取今天的活动
        var activities = DatabaseHelper.GetActivitiesByDate(DateTime.Today);
        if (activities.Count == 0) return;

        // 一天的总秒数
        const double totalSeconds = 24 * 3600;

        foreach (var act in activities)
        {
            if (act.IsIdle) continue; // 空闲不画

            // 计算色块位置和宽度
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double durSec = act.Duration;
            double x = (startSec / totalSeconds) * width;
            double w = Math.Max((durSec / totalSeconds) * width, 1); // 至少 1px

            // 获取颜色
            string colorHex = CategoryColors.TryGetValue(act.Category, out var c) ? c : "#7F8C8D";
            var color = (Color)ColorConverter.ConvertFromString(colorHex);

            var block = new Rectangle
            {
                Width = w,
                Height = height,
                Fill = new SolidColorBrush(color),
                ToolTip = $"{act.StartTime:HH:mm:ss} → {act.EndTime:HH:mm:ss}\n{act.ProcessName}\n{act.Category} · {FormatDuration(act.Duration)}"
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, 0);
            TimelineCanvas.Children.Add(block);
        }
    }

    /// <summary>
    /// 画分类图例
    /// </summary>
    private void DrawLegend()
    {
        LegendPanel.Children.Clear();
        foreach (var kvp in CategoryColors)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };

            var rect = new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kvp.Value)),
                RadiusX = 2,
                RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new TextBlock
            {
                Text = kvp.Key,
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(rect);
            panel.Children.Add(text);
            LegendPanel.Children.Add(panel);
        }
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m{seconds % 60}s";
        return $"{seconds / 3600}h{(seconds % 3600) / 60}m";
    }
}

/// <summary>
/// 给 ListView 用的显示模型
/// </summary>
public class ActivityDisplayItem
{
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime StartTime { get; set; }
    public string DurationText { get; set; } = "";
}
