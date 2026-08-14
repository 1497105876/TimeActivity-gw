using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TimeActivity.Data;
using TimeActivity.Services;
using TimeActivity.Helpers;
using TimeActivity.Rendering;

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
        LoadCategoryFilter();
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
        {
            RangeText.Text = s.ToString("MM-dd") + (s == DateTime.Today ? "（今天）" : "");
            AITitle.Text = "AI 每日总结";
            TrendSection.Visibility = Visibility.Collapsed;
        }
        else if (_period == "week")
        {
            RangeText.Text = $"{s:MM-dd} ~ {e:MM-dd}";
            AITitle.Text = "AI 每周总结";
            TrendSection.Visibility = Visibility.Visible;
        }
        else
        {
            RangeText.Text = s.ToString("yyyy-MM");
            AITitle.Text = "AI 每月总结";
            TrendSection.Visibility = Visibility.Visible;
        }

        // 加载 AI 总结
        LoadAISummary();
    }

    /// <summary>
    /// 判断当前周期是否是本周/本月
    /// </summary>
    private bool IsCurrentPeriod()
    {
        var (start, end) = GetRange();
        if (_period == "day")
            return _periodStart == DateTime.Today;
        if (_period == "week")
        {
            var todayWeekStart = DateHelper.GetWeekStart(DateTime.Today);
            return start == todayWeekStart;
        }
        // month
        return _periodStart.Year == DateTime.Today.Year && _periodStart.Month == DateTime.Today.Month;
    }



    /// <summary>
    /// 从数据库加载 AI 总结并显示
    /// </summary>
    private void LoadAISummary()
    {
        // 切换周期时重置按钮文字（避免"正在生成..."串到其他周期）
        if (_isGenerating && _generatingPeriod != _period)
            BtnGenerateAI.Content = "生成总结";
        else if (_isGenerating && _generatingPeriod == _period)
            BtnGenerateAI.Content = "正在生成...";

        string summaryType = _period switch { "week" => "weekly", "month" => "monthly", _ => "daily" };

        if (_period == "day")
        {
            // 日总结：查 manual
            var (text, createdAt) = AISummaryRepository.GetWithMeta(_periodStart, summaryType, "manual");
            if (text != null)
            {
                AISummaryText.Markdown = text;
                AISummaryTime.Text = FormatSummaryTime(createdAt);
                _currentAISummary = text;
            }
            else
            {
                AISummaryText.Markdown = "点击「生成总结」获取 AI 分析...";
                AISummaryTime.Text = "";
                _currentAISummary = null;
            }
            BtnGenerateAI.Visibility = Visibility.Visible;
        }
        else
        {
            // 周/月总结
            if (IsCurrentPeriod())
            {
                // 本周/月：查 manual，显示生成按钮
                var (text, createdAt) = AISummaryRepository.GetWithMeta(_periodStart, summaryType, "manual");
                if (text != null)
                {
                    AISummaryText.Markdown = text;
                    AISummaryTime.Text = FormatSummaryTime(createdAt);
                    _currentAISummary = text;
                }
                else
                {
                    AISummaryText.Markdown = "点击「生成总结」获取 AI 分析...";
                    AISummaryTime.Text = "";
                    _currentAISummary = null;
                }
                BtnGenerateAI.Visibility = Visibility.Visible;
            }
            else
            {
                // 非本周/月：查 auto，隐藏生成按钮
                var (text, createdAt) = AISummaryRepository.GetWithMeta(_periodStart, summaryType, "auto");
                if (text != null)
                {
                    AISummaryText.Markdown = text;
                    AISummaryTime.Text = FormatSummaryTime(createdAt);
                    _currentAISummary = text;
                }
                else
                {
                    // 没有 auto 总结记录，只显示提示，不写数据库
                    string hint = _period == "week" ? "上周的总结将在下次启动程序时自动生成。" : "上个月的总结将在下次启动程序时自动生成。";
                    AISummaryText.Markdown = hint;
                    AISummaryTime.Text = "";
                    _currentAISummary = null;
                }
                BtnGenerateAI.Visibility = Visibility.Hidden;
            }
        }
    }

    /// <summary>
    /// 格式化总结时间显示，如 "8/3 20:30 总结"
    /// </summary>
    private static string FormatSummaryTime(string? createdAt)
    {
        if (string.IsNullOrEmpty(createdAt)) return "";
        try
        {
            // CreatedAt 格式：yyyy-MM-dd HH:mm:ss.fff
            var dt = DateTime.Parse(createdAt);
            return $"{dt:M/d} {dt:HH:mm} 总结";
        }
        catch (Exception ex) { Logger.Error("格式化总结时间失败", ex); return ""; }
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

    // ========== 分类筛选加载 ==========

    private void LoadCategoryFilter()
    {
        CategoryFilter.Items.Clear();
        var allItem = new ComboBoxItem { Content = "全部分类", Tag = "", IsSelected = true };
        CategoryFilter.Items.Add(allItem);
        try
        {
            var cats = CategoryRepository.GetAll();
            foreach (var cat in cats)
            {
                CategoryFilter.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Name });
            }
        }
        catch (Exception ex) { Logger.Error("加载分类筛选列表失败", ex); }
        CategoryFilter.SelectedIndex = 0;
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

        bool includeIdle = ChkSkipIdle.IsChecked != true;
        string filterCategory = GetSelectedFilterCategory();

        Dictionary<string, int> catData;
        Dictionary<string, int> procData;
        Dictionary<string, int> dailyData;

        if (_period == "day")
        {
            // 日模式查原始表（数据量小，且需要明细）
            catData = ActivityRepository.GetCategorySummaryByRange(start, end, includeIdle);
            procData = ActivityRepository.GetProcessSummaryByRange(start, end, includeIdle);
            dailyData = ActivityRepository.GetDailyTotalsByRange(start, end, includeIdle);

            // 类别筛选
            if (!string.IsNullOrEmpty(filterCategory))
            {
                catData = catData.Where(k => k.Key == filterCategory)
                    .ToDictionary(k => k.Key, v => v.Value);
                procData = FilterProcessByCategory(start, end, filterCategory);
            }
        }
        else
        {
            // 周/月模式读汇总表（大幅减少扫描行数）
            catData = DailySummaryRepository.GetCategorySummary(start, end);
            dailyData = DailySummaryRepository.GetDailyTotals(start, end, includeIdle);

            // Top 应用：有筛选则按类别查，否则查全部
            procData = DailySummaryRepository.GetProcessSummary(start, end,
                string.IsNullOrEmpty(filterCategory) ? null : filterCategory);

            // 类别筛选时只保留选中的类别
            if (!string.IsNullOrEmpty(filterCategory))
            {
                catData = catData.Where(k => k.Key == filterCategory)
                    .ToDictionary(k => k.Key, v => v.Value);
            }
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

        // 日模式筛选了某类别时隐藏类别占比栏
        if (_period == "day" && !string.IsNullOrEmpty(filterCategory))
            CategorySection.Visibility = Visibility.Collapsed;
        else
            CategorySection.Visibility = Visibility.Visible;

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
        LoadData();
    }

    private Dictionary<string, int> FilterProcessByCategory(DateTime start, DateTime end, string category)
    {
        // 日模式 start==end，查询需要 end+1 天
        DateTime queryEnd = start.Date == end.Date ? end.AddDays(1) : end;
        var activities = ActivityRepository.GetByRange(start, queryEnd);
        return activities
            .Where(a => a.Category == category)
            .GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Sum(a => a.Duration))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Duration));
    }

    /// <summary>
    /// MarkdownScrollViewer 滚轮事件转交给外层 ScrollViewer
    /// </summary>
    private void AISummaryText_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // 标记已处理，不让 MarkdownScrollViewer 自己处理
        e.Handled = true;
        // 手动滚动外层 ScrollViewer
        MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta);
    }

    // ========== AI 总结 ==========

    // 防重复点击：上次调用时间
    private DateTime _lastAICallTime = DateTime.MinValue;
    // Cooldown 秒数（后续可从设置读）
    private const int AICooldownSeconds = 30;
    // 当前 AI 总结内容
    private string? _currentAISummary = null;
    private bool _isGenerating = false;
    private string? _generatingPeriod = null; // 记录正在生成的是哪个周期

    private async void BtnGenerateAI_Click(object sender, RoutedEventArgs e)
    {
        // 防重复点击 — Cooldown 机制
        var elapsed = (DateTime.Now - _lastAICallTime).TotalSeconds;
        if (elapsed < AICooldownSeconds)
        {
            AISummaryText.Markdown = $"请稍候，距离上次生成不足 {AICooldownSeconds} 秒（还剩 {(int)(AICooldownSeconds - elapsed)} 秒）";
            return;
        }
        _lastAICallTime = DateTime.Now;

        // 生成中不改 IsEnabled（会变白），用 flag + 文字提示
        if (_isGenerating) return;
        _isGenerating = true;
        BtnGenerateAI.Content = "正在生成...";
        AISummaryText.Markdown = "正在生成...";

        // 锁定当前 period 和日期，防止 await 期间用户切换导致串台
        string lockPeriod = _period;
        DateTime lockPeriodStart = _periodStart;
        _generatingPeriod = lockPeriod;

        try
        {
            var aiService = new AISummaryService();

            // 根据锁定时的 period 调对应方法
            string? result;
            if (lockPeriod == "day")
                result = await aiService.GenerateDailySummary(lockPeriodStart);
            else if (lockPeriod == "week")
                result = await aiService.GenerateWeeklySummary(lockPeriodStart);
            else
                result = await aiService.GenerateMonthlySummary(lockPeriodStart);

            if (result != null)
            {
                _currentAISummary = result;

                // 存入数据库
                string summaryType = lockPeriod switch { "week" => "weekly", "month" => "monthly", _ => "daily" };
                AISummaryRepository.Insert(lockPeriodStart, result, summaryType, "manual");

                // 自动保存到文件（按日期文件夹分，每次保留不覆盖）
                string? savePath = null;
                try
                {
                    savePath = AISummaryService.SaveSummaryToFile(result, lockPeriodStart, summaryType);
                }
                catch (Exception ex)
                {
                    Logger.Error("AI 总结自动保存失败", ex);
                }

                // 只有用户没切换走才刷新显示
                if (lockPeriod == _period && lockPeriodStart == _periodStart)
                {
                    LoadAISummary();
                }
            }
            else
            {
                AISummaryText.Markdown = "生成失败，请检查设置页中的 AI API 配置。";
                Logger.Error("AI 总结生成返回 null");
            }
        }
        catch (Exception ex)
        {
            AISummaryText.Markdown = $"生成失败：{ex.Message}";
            Logger.Error("AI 总结生成异常", ex);
        }
        finally
        {
            _isGenerating = false;
            _generatingPeriod = null;
            BtnGenerateAI.Content = "生成总结";
        }
    }
}
