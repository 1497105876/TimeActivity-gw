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

/// <summary>
/// 统计报表页 — 提供日/周/月三个维度的活动数据可视化，包括类别占比、
/// 每日趋势折线图、Top 应用排行，以及 AI 总结生成。
/// </summary>
public partial class StatisticsPage : Page
{
    // 当前查看的周期模式：day / week / month
    private string _period = "day";

    // 当前周期的参考日期（日模式=当天，周模式=该周任意一天，月模式=该月任意一天）
    private DateTime _periodStart = DateTime.Today;

    private CategoryColorHelper _colorHelper = new();
    private ChartRenderer _chartRenderer;

    // 分类名 → 颜色十六进制字符串
    private Dictionary<string, string> _categoryColors = new();

    // 缓存趋势数据，窗口 SizeChanged 时重绘用
    private Dictionary<string, int> _cachedDailyData = new();
    private DateTime _cachedRangeStart;
    private DateTime _cachedRangeEnd;

    /// <summary>
    /// 构造函数：初始化颜色、图表渲染器，加载分类筛选和默认数据
    /// </summary>
    public StatisticsPage()
    {
        InitializeComponent();
        _categoryColors = _colorHelper.Load();
        _chartRenderer = new ChartRenderer(_colorHelper);
        LoadCategoryFilter();
        RbDay.IsChecked = true; // 默认选日模式
        UpdateRange();
        LoadData();
    }

    /// <summary>
    /// 外部调用的刷新方法：重新加载颜色和当前周期数据（设置保存后用）
    /// </summary>
    public void RefreshData()
    {
        // 重建颜色助手确保不残留旧缓存
        _colorHelper = new CategoryColorHelper();
        _categoryColors = _colorHelper.Load();
        _chartRenderer.SetColorHelper(_colorHelper);
        LoadCategoryFilter();
        LoadData();
    }

    // ========== 周期切换 ==========

    /// <summary>
    /// 日/周/月单选按钮切换事件
    /// </summary>
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

    /// <summary>
    /// 根据当前周期模式和参考日期，计算实际的日期范围（起止日期）
    /// </summary>
    /// <returns>(起始日期, 结束日期)</returns>
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

    /// <summary>
    /// 更新日期范围显示文字、AI 总结标题、趋势图可见性，并加载 AI 总结
    /// </summary>
    private void UpdateRange()
    {
        var (s, e) = GetRange();
        // 根据周期模式设置显示文字和标题
        if (_period == "day")
        {
            RangeText.Text = s.ToString("MM-dd") + (s == DateTime.Today ? "（今天）" : "");
            AITitle.Text = "AI 每日总结";
            // 日模式不需要趋势图
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

        // 加载对应周期的 AI 总结
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
        bool currentGen = _generatingByPeriod.TryGetValue(_period, out bool g) && g;
        if (currentGen && _generatingPeriod == _period)
            BtnGenerateAI.Content = "正在生成...";
        else
            BtnGenerateAI.Content = "生成总结";

        string summaryType = _period switch { "week" => "weekly", "month" => "monthly", _ => "daily" };

        // 用 GetRange 算出来的起始日期查 AI 总结，而不是直接用 _periodStart
        // 因为 _periodStart 在周模式下可能是周中某天，但 AI 总结是按周一（周起始日）存的
        var (rangeStart, _) = GetRange();

        if (_period == "day")
        {
            // 日总结：查 manual
            var (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "manual");
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
                var (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "manual");
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
                var (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "auto");
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

    // ========== 分类筛选 ==========

    /// <summary>
    /// 加载分类筛选下拉框，第一项是"全部分类"
    /// </summary>
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

    /// <summary>
    /// 画布尺寸变化时用缓存数据重绘趋势图
    /// </summary>
    private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_cachedDailyData.Count > 0 || _cachedRangeStart != default)
            _chartRenderer.DrawTrendChart(TrendCanvas, _cachedDailyData, _cachedRangeStart, _cachedRangeEnd);
    }

    /// <summary>
    /// 从数据库加载当前周期的统计数据，刷新所有图表
    /// </summary>
    private void LoadData()
    {
        var (start, end) = GetRange();

        // 是否包含空闲时段、是否筛选了某个分类
        bool includeIdle = ChkSkipIdle.IsChecked != true;
        string filterCategory = GetSelectedFilterCategory();

        Dictionary<string, int> catData;
        Dictionary<string, int> procData;
        Dictionary<string, int> dailyData;

        if (_period == "day")
        {
            // 日模式直接查原始活动表（数据量小，需要明细）
            catData = ActivityRepository.GetCategorySummaryByRange(start, end, includeIdle);
            procData = ActivityRepository.GetProcessSummaryByRange(start, end, includeIdle);
            dailyData = ActivityRepository.GetDailyTotalsByRange(start, end, includeIdle);

            // 筛选了特定分类时，只保留该分类的数据
            if (!string.IsNullOrEmpty(filterCategory))
            {
                catData = catData.Where(k => k.Key == filterCategory)
                    .ToDictionary(k => k.Key, v => v.Value);
                procData = FilterProcessByCategory(start, end, filterCategory);
            }
        }
        else
        {
            // 周/月模式读每日汇总表（大幅减少扫描行数，性能好）
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

        // 计算总时长并显示
        int totalSeconds = catData.Values.Sum();
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);

        TotalText.Text = $"总活跃时长：{ts.Hours + ts.Days * 24}h{ts.Minutes}m";

        // 补充信息：日均时长
        if (_period == "day")
            DetailText.Text = "";
        else if (_period == "week")
            DetailText.Text = $"日均：{totalSeconds / 7 / 3600}h{totalSeconds / 7 % 3600 / 60}m";
        else
        {
            int days = DateTime.DaysInMonth(start.Year, start.Month);
            DetailText.Text = $"日均：{totalSeconds / days / 3600}h{totalSeconds / days % 3600 / 60}m";
        }

        // 日模式筛选了某分类时隐藏类别占比栏（因为只有一个分类没意义）
        if (_period == "day" && !string.IsNullOrEmpty(filterCategory))
            CategorySection.Visibility = Visibility.Collapsed;
        else
            CategorySection.Visibility = Visibility.Visible;

        // 画各类图表
        _chartRenderer.DrawCategoryBars(CategoryBarsPanel, catData, totalSeconds);

        // 缓存趋势数据，SizeChanged 时重绘
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

    /// <summary>
    /// 日模式下按分类筛选进程：查原始活动表，过滤出指定分类的进程
    /// </summary>
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

    // 当前显示的 AI 总结内容
    private string? _currentAISummary = null;
    // 按总结类型独立的生成状态：day/week/month → 是否正在生成
    private readonly Dictionary<string, bool> _generatingByPeriod = new() { ["day"] = false, ["week"] = false, ["month"] = false };
    // 记录正在生成的是哪个周期，防止 await 期间用户切换导致串台
    private string? _generatingPeriod = null;

    /// <summary>
    /// "生成总结"按钮点击事件：调用 AI 服务生成当前周期的总结，存库并自动保存文件
    /// </summary>
    private async void BtnGenerateAI_Click(object sender, RoutedEventArgs e)
    {
        string lockPeriod = _period;

        // 按总结类型独立防重复：日/周/月各自可并行，但单个类型正在生成时不能重复点
        if (_generatingByPeriod.TryGetValue(lockPeriod, out bool isGen) && isGen)
        {
            AISummaryText.Markdown = $"当前{(lockPeriod == "day" ? "日" : lockPeriod == "week" ? "周" : "月")}总结正在生成中，请等待完成。";
            return;
        }
        _generatingByPeriod[lockPeriod] = true;
        BtnGenerateAI.Content = "正在生成...";
        AISummaryText.Markdown = "正在生成...";

        // 锁定当前周期，防止 await 期间用户切换页面导致结果串台
        var (lockRangeStart, _) = GetRange();
        DateTime lockPeriodStart = lockRangeStart;
        _generatingPeriod = lockPeriod;

        try
        {
            var aiService = new AISummaryService();

            // 根据锁定的周期调对应方法
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

                // 自动保存到文件（按日期分文件夹，每次保留不覆盖）
                string? savePath = null;
                try
                {
                    savePath = AISummaryService.SaveSummaryToFile(result, lockPeriodStart, summaryType);
                }
                catch (Exception ex)
                {
                    Logger.Error("AI 总结自动保存失败", ex);
                }

                // 只有用户没切换走才刷新显示（用 GetRange 比较确保一致性）
                var (currentStart, _) = GetRange();
                if (lockPeriod == _period && lockPeriodStart == currentStart)
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
            Logger.Error("AI 总结生成异常", ex);
            try { AISummaryText.Markdown = "生成失败，请查看日志。"; }
            catch { /* UI 不可用时忽略 */ }
        }
        finally
        {
            _generatingByPeriod[lockPeriod] = false;
            _generatingPeriod = null;
            // 只有用户还在当前周期才重置按钮文字
            if (lockPeriod == _period)
                BtnGenerateAI.Content = "生成总结";
        }
    }
}
