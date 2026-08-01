using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity;

public partial class StatisticsPage : Page
{
    private string _period = "day"; // day / week / month
    private DateTime _periodStart = DateTime.Today;

    private Dictionary<string, string> _categoryColors = new();

    // 缓存当前趋势数据，SizeChanged 时重绘
    private Dictionary<string, int> _cachedDailyData = new();
    private DateTime _cachedRangeStart;
    private DateTime _cachedRangeEnd;

    public StatisticsPage()
    {
        InitializeComponent();
        LoadCategoryColors();
        // 从设置读取跳过空闲开关初始状态
        ChkSkipIdle.IsChecked = Data.DatabaseHelper.GetSetting("SkipIdleInStats", "false") == "true";
        RbDay.IsChecked = true;
        UpdateRange();
        LoadData();
    }

    private void LoadCategoryColors()
    {
        _categoryColors = new Dictionary<string, string>();
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db")}");
            conn.Open();
            using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                "SELECT Name, Color FROM Categories ORDER BY SortOrder", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                _categoryColors[reader.GetString(0)] = reader.GetString(1);
        }
        catch
        {
            _categoryColors = new Dictionary<string, string>
            {
                { "开发", "#4A90D9" }, { "社交", "#E67E22" }, { "娱乐", "#E74C3C" },
                { "学习", "#2ECC71" }, { "系统", "#95A5A6" }, { "网页", "#9B59B6" },
                { "空闲", "#BDC3C7" }, { "未分类", "#7F8C8D" },
            };
        }
    }

    private Color GetCategoryColor(string category)
    {
        if (_categoryColors.TryGetValue(category, out var hex))
            return (Color)ColorConverter.ConvertFromString(hex);
        return (Color)ColorConverter.ConvertFromString("#7F8C8D");
    }

    // ========== 期间切换 ==========

    private void RbPeriod_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var tag = (string)((RadioButton)sender).Tag;
        if (tag == _period) return;
        _period = tag;
        _periodStart = DateTime.Today;
        UpdateRange();
        LoadData();
    }

    private (DateTime start, DateTime end) GetRange()
    {
        switch (_period)
        {
            case "week":
                int delta = (int)_periodStart.DayOfWeek;
                if (delta == 0) delta = 7; // 周日归到本周末
                return (_periodStart.AddDays(-(delta - 1)), _periodStart.AddDays(7 - delta));
            case "month":
                var first = new DateTime(_periodStart.Year, _periodStart.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            default:
                return (_periodStart, _periodStart);
        }
    }

    private void UpdateRange()
    {
        var (s, e) = GetRange();
        if (_period == "day")
            RangeText.Text = s.ToString("MM-dd") + (s == DateTime.Today ? "（今天）" : "");
        else if (_period == "week")
            RangeText.Text = $"{s:MM-dd} ~ {e:MM-dd}";
        else
            RangeText.Text = s.ToString("yyyy-MM");
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        switch (_period)
        {
            case "week": _periodStart = _periodStart.AddDays(-7); break;
            case "month": _periodStart = _periodStart.AddMonths(-1); break;
            default: _periodStart = _periodStart.AddDays(-1); break;
        }
        UpdateRange();
        LoadData();
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        var (s, end) = GetRange();
        if (_period == "day" && _periodStart >= DateTime.Today) return;
        if (_period == "week" && end >= DateTime.Today) return;
        if (_period == "month" && end >= DateTime.Today) return;

        switch (_period)
        {
            case "week": _periodStart = _periodStart.AddDays(7); break;
            case "month": _periodStart = _periodStart.AddMonths(1); break;
            default: _periodStart = _periodStart.AddDays(1); break;
        }
        UpdateRange();
        LoadData();
    }

    private void BtnThis_Click(object sender, RoutedEventArgs e)
    {
        _periodStart = DateTime.Today;
        UpdateRange();
        LoadData();
    }

    // ========== 数据加载 ==========

    private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_cachedDailyData.Count > 0 || _cachedRangeStart != default)
            DrawTrendChart(_cachedDailyData, _cachedRangeStart, _cachedRangeEnd);
    }

    private void LoadData()
    {
        var (start, end) = GetRange();

        bool includeIdle = ChkSkipIdle.IsChecked == true;
        var catData = DatabaseHelper.GetCategorySummaryByRange(start, end, includeIdle);
        var procData = DatabaseHelper.GetProcessSummaryByRange(start, end, includeIdle);
        var dailyData = DatabaseHelper.GetDailyTotalsByRange(start, end, includeIdle);

        // 类别筛选
        string filterCategory = GetSelectedFilterCategory();
        if (!string.IsNullOrEmpty(filterCategory))
        {
            catData = catData.Where(k => k.Key == filterCategory)
                .ToDictionary(k => k.Key, v => v.Value);
            procData = FilterProcessByCategory(start, end, filterCategory);
        }

        int totalSeconds = catData.Values.Sum();
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);

        TotalText.Text = $"总活跃时长：{ts.Hours + ts.Days * 24}h{ts.Minutes}m";

        // 明细
        if (_period == "day")
            DetailText.Text = "";
        else if (_period == "week")
            DetailText.Text = $"日均：{totalSeconds / 7 / 3600}h{totalSeconds / 7 % 3600 / 60}m";
        else
        {
            int days = DateTime.DaysInMonth(start.Year, start.Month);
            DetailText.Text = $"日均：{totalSeconds / days / 3600}h{totalSeconds / days % 3600 / 60}m";
        }

        DrawCategoryBars(catData, totalSeconds);

        _cachedDailyData = dailyData;
        _cachedRangeStart = start;
        _cachedRangeEnd = end;
        DrawTrendChart(dailyData, start, end);

        DrawTopApps(procData);
    }

    private string GetSelectedFilterCategory()
    {
        if (CategoryFilter?.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "";
        return "";
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBarsPanel == null) return;
        LoadData();
    }

    private void ChkSkipIdle_Changed(object sender, RoutedEventArgs e)
    {
        if (CategoryBarsPanel == null) return;
        LoadData();
    }

    private Dictionary<string, int> FilterProcessByCategory(DateTime start, DateTime end, string category)
    {
        var activities = DatabaseHelper.GetActivitiesByRange(start, end);
        return activities
            .Where(a => a.Category == category)
            .GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Duration));
    }

    // ========== 类别条形图 ==========

    private void DrawCategoryBars(Dictionary<string, int> data, int totalSeconds)
    {
        CategoryBarsPanel.Children.Clear();

        if (data.Count == 0)
        {
            CategoryBarsPanel.Children.Add(new TextBlock
            {
                Text = "暂无数据",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 12
            });
            return;
        }

        foreach (var kvp in data)
        {
            var color = GetCategoryColor(kvp.Key);
            double pct = totalSeconds > 0 ? (double)kvp.Value / totalSeconds : 0;
            string durStr = FormatDuration(kvp.Value);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            // 类别名
            var name = new TextBlock
            {
                Text = kvp.Key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            // 条形
            var barBg = new Border
            {
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(barBg, 1);

            var barFill = new Border
            {
                Height = 18,
                Width = Math.Max(pct * 100, 2),
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            barBg.Child = barFill;
            row.Children.Add(barBg);

            // 时长
            var dur = new TextBlock
            {
                Text = durStr, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 2);
            row.Children.Add(dur);

            // 百分比
            var pctText = new TextBlock
            {
                Text = $"{pct * 100:F1}%", FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctText, 3);
            row.Children.Add(pctText);

            CategoryBarsPanel.Children.Add(row);
        }
    }

    // ========== 每日趋势折线图 ==========

    private void DrawTrendChart(Dictionary<string, int> dailyData, DateTime start, DateTime end)
    {
        TrendCanvas.Children.Clear();

        double w = TrendCanvas.ActualWidth;
        if (w <= 0) w = 800;
        double h = TrendCanvas.Height;

        int days = (end - start).Days + 1;
        if (days <= 1) days = 1;

        // 找最大值
        int maxSec = dailyData.Values.Count > 0 ? dailyData.Values.Max() : 3600;
        if (maxSec <= 0) maxSec = 3600;

        // 背景刻度线
        for (int i = 0; i <= 4; i++)
        {
            double y = h - 16 - (h - 32) * i / 4.0;
            var line = new Line
            {
                X1 = 40, Y1 = y, X2 = w, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                StrokeThickness = 1
            };
            TrendCanvas.Children.Add(line);

            int hours = (int)(maxSec * i / 4.0 / 3600);
            var label = new TextBlock
            {
                Text = $"{hours}h", FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 6);
            TrendCanvas.Children.Add(label);
        }

        // 柱状图（每天一根柱子）
        double barW = (w - 48) / days;
        for (int i = 0; i < days; i++)
        {
            DateTime day = start.AddDays(i);
            string key = day.ToString("yyyy-MM-dd");
            int sec = dailyData.ContainsKey(key) ? dailyData[key] : 0;

            double x = 40 + i * barW + 2;
            double barH = sec > 0 ? (h - 32) * ((double)sec / maxSec) : 0;
            double y = h - 16 - barH;

            if (sec > 0)
            {
                var bar = new Rectangle
                {
                    Width = Math.Max(barW - 4, 2),
                    Height = barH,
                    Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                    RadiusX = 2, RadiusY = 2
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, y);
                TrendCanvas.Children.Add(bar);
            }

            // X轴标签（只在柱子够宽时显示）
            if (barW >= 30)
            {
                var label = new TextBlock
                {
                    Text = day.ToString("MM-dd"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, h - 14);
                TrendCanvas.Children.Add(label);
            }
        }
    }

    // ========== Top 应用 ==========

    private void DrawTopApps(Dictionary<string, int> data)
    {
        TopAppsPanel.Children.Clear();

        if (data.Count == 0)
        {
            TopAppsPanel.Children.Add(new TextBlock
            {
                Text = "暂无数据",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 12
            });
            return;
        }

        int top = Math.Min(data.Count, 15);
        int maxSec = data.Values.First();

        int i = 0;
        foreach (var kvp in data.Take(top))
        {
            double pct = maxSec > 0 ? (double)kvp.Value / maxSec : 0;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            // 排名
            var rank = new TextBlock
            {
                Text = $"{i + 1}", FontSize = 12, FontWeight = FontWeight.FromOpenTypeWeight(700),
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rank, 0);
            row.Children.Add(rank);

            // 进程名
            var name = new TextBlock
            {
                Text = kvp.Key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            // 条形
            var barBg = new Border
            {
                Height = 14,
                Background = new SolidColorBrush(Color.FromArgb(30, 0x4A, 0x90, 0xD9)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(barBg, 2);
            var barFill = new Border
            {
                Height = 14,
                Width = Math.Max(pct * 100, 2),
                Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            barBg.Child = barFill;
            row.Children.Add(barBg);

            // 时长
            var dur = new TextBlock
            {
                Text = FormatDuration(kvp.Value), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, 3);
            row.Children.Add(dur);

            TopAppsPanel.Children.Add(row);
            i++;
        }
    }

    // ========== 工具 ==========

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m";
        return $"{seconds / 3600}h{(seconds % 3600) / 60}m";
    }

    // ========== AI 总结 ==========

    private async void BtnGenerateAI_Click(object sender, RoutedEventArgs e)
    {
        BtnGenerateAI.IsEnabled = false;
        AISummaryText.Text = "正在生成...";

        try
        {
            var aiService = new AISummaryService();

            // 先查数据库有没有已存的总结
            DateTime summaryDate = _period switch
            {
                "day" => _periodStart,
                "week" => _periodStart,
                _ => _periodStart,
            };

            var existing = DatabaseHelper.GetAISummary(summaryDate);
            if (existing != null)
            {
                AISummaryText.Text = existing;
                BtnGenerateAI.IsEnabled = true;
                return;
            }

            string? result = await aiService.GenerateDailySummary(summaryDate);
            if (result != null)
            {
                AISummaryText.Text = result;
                DatabaseHelper.InsertAISummary(summaryDate, result);
            }
            else
            {
                AISummaryText.Text = "生成失败，请检查设置页中的 AI API 配置。";
            }
        }
        catch (Exception ex)
        {
            AISummaryText.Text = $"生成失败：{ex.Message}";
        }
        finally
        {
            BtnGenerateAI.IsEnabled = true;
        }
    }
}
