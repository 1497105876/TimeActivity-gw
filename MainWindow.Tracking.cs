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

// ============================================================================
// MainWindow.Tracking.cs — 主窗口的"追踪与数据刷新"部分类
// 职责：
//   1) 启动时的数据维护（按保留天数清理旧数据、补全每日汇总）；
//   2) 自动补生成上周/上月的 AI 总结（每天只检查一次）；
//   3) 开始/停止追踪按钮逻辑与托盘状态同步；
//   4) 日期切换(昨天/今天/刷新)与活动列表加载；
//   5) 追踪引擎事件的 UI 回调（实时状态、新记录插入、防抖重绘）。
// 协作对象：TrackingEngine(事件源)、ActivityRepository/DailySummaryRepository/
//           AISummaryRepository(数据)、DatabaseHelper(清理)、Logger(日志)。
// ============================================================================
public partial class MainWindow
{
    /// <summary>
    /// 数据保留策略执行：删除超过 N 天的旧活动记录，并补全缺失的每日汇总。
    /// 在应用启动时调用一次；任何异常都只记日志，不影响主流程启动。
    /// </summary>
    private void PerformDataRetention()
    {
        try
        {
            // 读取用户配置的保留天数（默认 90 天）
            string? retentionStr = SettingsRepository.Get("DataRetentionDays", "90");
            // 只有解析成功且大于 0 才执行清理（0 或非法值表示不自动清理）
            if (int.TryParse(retentionStr, out int days) && days > 0)
            {
                // 调用数据库层清理过期数据，返回实际删除的行数
                int deleted = DatabaseHelper.CleanOldData(days);
                if (deleted > 0) // 有删除才提示，避免每次启动都打扰用户
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
    /// 自动补生成"上周/上月"AI 总结：每天首次自动刷新时各检查一次，
    /// 有活动数据才调用 AI 生成；无数据写入占位文案，避免报表页一直显示"进行中"。
    /// 失败只记日志，不影响主流程。
    /// </summary>
    private async Task CheckAutoSummaryAsync()
    {
        // 同一天只执行一次，避免 30 秒刷新重复查库
        if (_lastAutoSummaryCheckDate == DateTime.Today) return;
        // 立即记录本次检查日期（先赋值再执行，防止后续异常导致同日反复重试）
        _lastAutoSummaryCheckDate = DateTime.Today;
        Logger.Info("开始检查自动周/月总结...");

        try
        {
            // 总开关关闭则直接跳过（设置页可开关 AI 功能）
            if (SettingsRepository.Get("EnableAI", "true") != "true")
            {
                Logger.Info("AI 未启用，跳过自动总结");
                return;
            }

            // 每次新建服务实例（内部读取 API 配置），避免长期持有连接/状态
            var aiService = new AISummaryService();
            DateTime today = DateTime.Today;

            // 检查上周总结：今天回退 7 天后取其所在周的周一
            DateTime lastWeekStart = DateHelper.GetWeekStart(today.AddDays(-7));
            Logger.Info($"检查上周总结：lastWeekStart={lastWeekStart:yyyy-MM-dd}, HasAuto={AISummaryRepository.HasAuto(lastWeekStart, "weekly")}");
            if (!AISummaryRepository.HasAuto(lastWeekStart, "weekly"))
            {
                // 统计上周一~上周日的分类活跃总秒数（不含空闲），判断该周是否有数据
                int weekSeconds = ActivityRepository.GetCategorySummaryByRange(lastWeekStart, lastWeekStart.AddDays(6), false).Values.Sum();
                if (weekSeconds > 0) // 有活动数据才值得调用 AI 生成总结
                {
                    Logger.Info($"补生成上周总结：{lastWeekStart:yyyy-MM-dd}");
                    // 调用 AI 服务异步生成周报正文
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

            // 检查上月总结：取本月 1 号再回退一个月，即上个月 1 号
            DateTime lastMonthStart = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            Logger.Info($"检查上月总结：lastMonthStart={lastMonthStart:yyyy-MM-dd}, HasAuto={AISummaryRepository.HasAuto(lastMonthStart, "monthly")}");
            if (!AISummaryRepository.HasAuto(lastMonthStart, "monthly"))
            {
                // 统计上月 1 日~末日的分类活跃总秒数（区间右端 = 本月1号-1天 = 上月末天）
                int monthSeconds = ActivityRepository.GetCategorySummaryByRange(lastMonthStart, lastMonthStart.AddMonths(1).AddDays(-1), false).Values.Sum();
                if (monthSeconds > 0) // 上月有数据才生成，否则写占位文案
                {
                    Logger.Info($"补生成上月总结：{lastMonthStart:yyyy-MM-dd}");
                    // 调用 AI 服务异步生成月报正文
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
    /// 在状态栏显示一条临时消息，3.5 秒后自动清空。
    /// 每次调用都会新建一个一次性计时器（不复用），简单但频繁调用会产生多个计时器。
    /// </summary>
    private void ShowStatus(string message)
    {
        StatusBar.Text = message; // 立即显示消息
        // 创建 3.5 秒后触发一次的 UI 计时器用于清除消息
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (s, e) =>
        {
            StatusBar.Text = "";   // 清空状态栏文本
            timer.Stop();          // 停止并释放计时器
        };
        // 启动计时器，到期清空状态栏文字
        timer.Start();
    }

    /// <summary>开始追踪：启动引擎；若开启了截图功能则同时启动截图服务，并刷新按钮状态。</summary>
    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        AppServices.StartTracking();  // 统一入口：引擎+按配置启动截图（2026-08-23）
        RefreshTrackingButtons();     // 按钮与状态文字对齐
        (Application.Current as App)?.Host?.UpdateTooltip(); // 托盘提示同步
    }

    /// <summary>停止追踪：同时停止引擎与截图服务，并恢复按钮初始状态。</summary>
    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        AppServices.StopTracking();   // 统一入口：停止采样（结算落库）+ 停止截图
        RefreshTrackingButtons();     // 按钮与状态文字对齐
        (Application.Current as App)?.Host?.UpdateTooltip(); // 托盘提示同步
    }

    /// <summary>刷新按钮：按当前日期重新加载数据（视为切日，重置勾选）。</summary>
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadDateData(_currentDate, isDateChange: true);

    /// <summary>前一天按钮：日期回退一天并重新加载。</summary>
    private void BtnPrevDay_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = _currentDate.AddDays(-1); // 回退一天
        LoadDateData(_currentDate, isDateChange: true);
    }

    /// <summary>后一天按钮：日期前进一天并重新加载（不允许超过今天）。</summary>
    private void BtnNextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDate >= DateTime.Today) return; // 已在今天则不能再往后
        _currentDate = _currentDate.AddDays(1);     // 前进一天
        LoadDateData(_currentDate, isDateChange: true);
    }

    /// <summary>"今天"按钮：直接跳回今天并重新加载。</summary>
    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = DateTime.Today; // 重置为今天
        LoadDateData(_currentDate, isDateChange: true);
    }

    /// <summary>
    /// 追踪引擎 OnStatusChanged 事件的 UI 回调：更新底部"当前应用 — 标题 / 分类"。
    /// </summary>
    private void OnStatusChanged(string process, string title, string category)
    {
        // 引擎在后台线程触发事件，必须切回 UI 线程才能改控件
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"{process} — {title}"; // 当前前台应用与窗口标题
            CategoryText.Text = category;             // 分类器给出的分类名
        });
    }

    /// <summary>
    /// 追踪引擎 OnActivityRecorded 事件的 UI 回调：把新完成的活动插入列表顶部，
    /// 并触发防抖刷新（重绘时间轴/统计）。仅当正在查看"今天"时才实时插入。
    /// </summary>
    private void OnActivityRecorded(ActivityRecord activity)
    {
        // Dispatcher.BeginInvoke: 切回 UI 线程更新界面
        Dispatcher.BeginInvoke(() =>
        {
            if (_currentDate == DateTime.Today) // 只在浏览今天时做实时插入
            {
                _items.Insert(0, CreateDisplayItem(activity)); // 新记录置顶
                while (_items.Count > 500)                     // 列表上限 500 条，防止内存无限增长
                    _items.RemoveAt(_items.Count - 1);         // 淘汰最旧的记录
            }
            ScheduleDebounceRefresh(); // 合并短时间内的多次事件为一次重绘
        });
    }

    /// <summary>
    /// 防抖刷新：DebounceMs 毫秒内的多次请求合并为一次真正的重载+重绘。
    /// 避免活动高频产生时反复查库/重绘造成 UI 卡顿。
    /// </summary>
    private void ScheduleDebounceRefresh()
    {
        if (_debounceTimer == null) // 懒初始化：第一次调用时才创建计时器
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMs)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer!.Stop(); // 到期先停表（本次刷新完成后不再重复触发）
                if (_currentDate == DateTime.Today) // 仍停留在"今天"才需要实时刷新
                {
                    _cachedActivities = ActivityRepository.GetByDate(DateTime.Today); // 重查今日数据并更新缓存
                    DrawAll();          // 重绘时间轴等所有视图
                    UpdateTodayTotal(); // 刷新今日总时长显示
                }
            };
        }
        _debounceTimer.Stop();  // 重启计时器实现"防抖"：每次新事件都重新计时
        _debounceTimer.Start();
    }

    /// <summary>
    /// 加载指定日期的数据：刷新日期文字、按钮可用性、活动列表与缓存，
    /// 并重绘时间轴/统计视图。切日(isDateChange=true)时还会重置勾选并重建统计列表。
    /// </summary>
    /// <param name="date">要加载的日期</param>
    /// <param name="isDateChange">true=用户切换了日期；false=同日内自动刷新</param>
    private void LoadDateData(DateTime date, bool isDateChange = false)
    {
        // 设置日期显示文字
        if (date == DateTime.Today) // 今天显示友好文案
            DateText.Text = "今天";
        else if (date == DateTime.Today.AddDays(-1)) // 昨天同样用文案
            DateText.Text = "昨天";
        else
            DateText.Text = date.ToString("MM-dd"); // 其余显示 MM-dd

        // 下一天按钮在查看今天时禁用（没有"未来"数据可看）
        BtnNextDay.IsEnabled = date < DateTime.Today;

        // 清空旧列表，倒序填充（最新的在最上面）
        _items.Clear();
        _screenshotPathCache.Clear(); // 换日期后旧截图缓存作废（2026-08-23）
        var activities = ActivityRepository.GetByDate(date); // 从库中按时间升序取出当日记录
        _cachedActivities = activities;                      // 同步更新渲染用的缓存
        foreach (var a in activities.AsEnumerable().Reverse()) // 反转成新→旧顺序
        {
            _items.Add(CreateDisplayItem(a)); // 逐条包装为列表显示模型
        }

        // 切日期时清空勾选 + 重建统计列表（自动刷新不动勾选）
        if (isDateChange)
        {
            _checkedApps.Clear();      // 清空应用勾选集合
            _checkedCategories.Clear();// 清空分类勾选集合
            if (StatsListPanel?.Visibility == Visibility.Visible) // 统计页可见才需要重建
                LoadStatsLists();
        }

        DrawAll();          // 重绘所有图表（时间轴/概览等）
        UpdateTodayTotal(); // 更新顶部总时长
    }

    /// <summary>
    /// 列表模式单选按钮切换：在"活动明细列表"与"使用统计列表"两个视图间切换。
    /// Tag="stats" 表示选中了统计模式，否则回到明细列表。
    /// </summary>
    private void RbListMode_Checked(object sender, RoutedEventArgs e)
    {
        if (ActivityList == null || StatsListPanel == null) return; // XAML 尚未加载完成时直接返回

        var rb = sender as RadioButton; // 取出触发事件的按钮
        if (rb?.Tag?.ToString() == "stats") // 选中"统计"模式
        {
            ActivityList.Visibility = Visibility.Collapsed;   // 隐藏明细列表
            StatsListPanel.Visibility = Visibility.Visible;   // 显示统计面板
            LoadStatsLists();                                 // 加载统计数据
        }
        else // 回到"活动"明细模式
        {
            ActivityList.Visibility = Visibility.Visible;     // 显示明细列表
            StatsListPanel.Visibility = Visibility.Collapsed; // 隐藏统计面板
        }
    }

}
