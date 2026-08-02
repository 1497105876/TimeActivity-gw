using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeActivity.Data;
using TimeActivity.Helpers;
using TimeActivity.Rendering;
using TimeActivity.Services;

namespace TimeActivity;

public partial class StatisticsPage : Page
{
    private string _period = "day"; // day / week / month
    private DateTime _periodStart = DateTime.Today;

    private readonly CategoryColorHelper _colorHelper = new();
    private readonly ChartRenderer _chartRenderer;

    private Dictionary<string, string> _categoryColors = new();

    // 缓存当前趋势数据，SizeChanged 时重绘
    private Dictionary<string, int> _cachedDailyData = new();
    private DateTime _cachedRangeStart;
    private DateTime _cachedRangeEnd;

    public StatisticsPage()
    {
        InitializeComponent();
        _categoryColors = _colorHelper.Load();
        _chartRenderer = new ChartRenderer(_colorHelper);
        // 从设置读取跳过空闲开关初始状态
        ChkSkipIdle.IsChecked = Data.DatabaseHelper.GetSetting("SkipIdleInStats", "false") == "true";
        RbDay.IsChecked = true;
        UpdateRange();
        LoadData();
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
            _chartRenderer.DrawTrendChart(TrendCanvas, _cachedDailyData, _cachedRangeStart, _cachedRangeEnd);
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

        _chartRenderer.DrawCategoryBars(CategoryBarsPanel, catData, totalSeconds);

        _cachedDailyData = dailyData;
        _cachedRangeStart = start;
        _cachedRangeEnd = end;
        _chartRenderer.DrawTrendChart(TrendCanvas, dailyData, start, end);

        _chartRenderer.DrawTopApps(TopAppsPanel, procData);
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
        // 回写设置，跟设置页同步
        Data.DatabaseHelper.SetSetting("SkipIdleInStats", ChkSkipIdle.IsChecked == true ? "true" : "false");
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

    // ========== AI 总结 ==========

    // 防重复点击：上次调用时间
    private DateTime _lastAICallTime = DateTime.MinValue;
    // Cooldown 秒数（后续可从设置读）
    private const int AICooldownSeconds = 30;
    // 当前 AI 总结内容（用于保存）
    private string? _currentAISummary = null;

    private async void BtnGenerateAI_Click(object sender, RoutedEventArgs e)
    {
        // 防重复点击 — Cooldown 机制
        var elapsed = (DateTime.Now - _lastAICallTime).TotalSeconds;
        if (elapsed < AICooldownSeconds)
        {
            AISummaryText.Text = $"请稍候，距离上次生成不足 {AICooldownSeconds} 秒（还剩 {(int)(AICooldownSeconds - elapsed)} 秒）";
            return;
        }
        _lastAICallTime = DateTime.Now;

        BtnGenerateAI.IsEnabled = false;
        BtnSaveAISummary.IsEnabled = false;
        AISummaryText.Text = "正在生成...";

        try
        {
            var aiService = new AISummaryService();

            DateTime summaryDate = _period switch
            {
                "day" => _periodStart,
                "week" => _periodStart,
                _ => _periodStart,
            };

            // 每次点击都重新生成，不读缓存
            string? result = await aiService.GenerateDailySummary(summaryDate);
            if (result != null)
            {
                AISummaryText.Text = result;
                _currentAISummary = result;
                BtnSaveAISummary.IsEnabled = true;

                // 存入数据库（覆盖旧的）
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

    private void BtnSaveAISummary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentAISummary))
        {
            AISummaryText.Text = "没有可保存的总结内容，请先生成。";
            return;
        }

        try
        {
            string? savePath = AISummaryService.SaveSummaryToFile(_currentAISummary, _periodStart);
            if (savePath != null)
            {
                AISummaryText.Text = $"{_currentAISummary}\n\n---\n已保存到：{savePath}";
            }
            else
            {
                AISummaryText.Text = $"{_currentAISummary}\n\n---\n保存失败。";
            }
        }
        catch (Exception ex)
        {
            AISummaryText.Text = $"{_currentAISummary}\n\n---\n保存失败：{ex.Message}";
        }
    }
}
