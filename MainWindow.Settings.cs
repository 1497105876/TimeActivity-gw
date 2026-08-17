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
                            try
                            {
                                DatabaseHelper.ReclassifyAll(_classifier.Classify);
                                // 底层数据已变，使近期自动总结失效并立即补算刷新
                                AISummaryRepository.InvalidateRecent();
                                _summaryScheduler.RegenerateNow();
                            }
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

    private static object? GetListViewItemFromPoint(System.Windows.Controls.ListView list, System.Windows.Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.ListViewItem)
            hit = VisualTreeHelper.GetParent(hit);
        return hit;
    }

    private void LoadCategoryColors()
    {
        _categoryColors = _colorHelper.Load();
    }

    private Color GetCategoryColor(string category)
    {
        return _colorHelper.GetColor(category);
    }

    private double GetContainerWidth()
    {
        double w = TimelineContainer.ActualWidth - 16; // 减去 Padding
        if (w <= 0) w = 880;
        return w;
    }

}
