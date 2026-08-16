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

public partial class MainWindow
{
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

}
