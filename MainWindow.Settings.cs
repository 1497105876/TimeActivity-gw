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
// MainWindow.Settings.cs — 主窗口的"设置入口与着色/右键菜单"部分类
// 职责：
//   1) 打开设置窗口（默认页或指定分区，如规则管理）；
//   2) 处理时间轴配色模式切换（按应用/按分类）；
//   3) 提供统一的取色逻辑 GetAppColor（应用色 or 分类色）；
//   4) 统计列表的右键菜单：改应用颜色、改应用所属分类、改分类颜色；
//   5) 颜色选择对话框封装与命中测试等小工具函数。
// 协作对象：SettingsWindow、AppColorAllocator、CategoryColorHelper、
//           CategoryRepository/RuleRepository、DatabaseHelper(全量重分类)。
// ============================================================================
public partial class MainWindow
{
    /// <summary>
    /// "设置"按钮点击：以模态方式打开设置窗口（默认起始页）。
    /// </summary>
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new SettingsWindow(); // 新建设置窗口实例
            win.Owner = this;               // 指定所有者窗口（居中/随动/最小化跟随）
            win.ShowDialog();               // 模态显示：关闭前阻塞本窗口交互
        }
        catch (Exception ex) // 设置窗口构造/初始化异常时给出完整错误信息
        {
            MessageBox.Show($"打开设置失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开设置窗口并直接定位到指定分区（如 "rules" 规则管理）。
    /// 与 BtnSettings_Click 的区别仅在于初始展示的分区不同。
    /// </summary>
    /// <param name="section">设置窗口内的目标分区标识</param>
    private void OpenSettings(string section)
    {
        try
        {
            var win = new SettingsWindow(section); // 传入分区名，窗口内部会导航到对应页面
            win.Owner = this;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开设置失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 配色模式单选按钮变化：保存设置并让所有渲染器/图例/统计列表按新模式重新取色刷新。
    /// </summary>
    private void ColorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // XAML 初始化阶段触发的事件直接忽略（控件尚未就绪）
        // 根据选中的单选按钮确定模式："app"=按应用着色，"category"=按分类着色
        _colorMode = RbColorApp.IsChecked == true ? "app" : "category";
        SettingsRepository.Set("ColorMode", _colorMode); // 持久化到设置表
        // 重新注入取色函数：时间轴与概览图渲染时都会回调它决定每段颜色
        _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
        LoadCategoryColors(); // 重新加载颜色
        DrawLegend();
        DrawAll();
        // 刷新统计列表
        LoadStatsLists();
    }

    /// <summary>
    /// 统一取色入口：根据当前配色模式返回某进程/分类应使用的颜色。
    /// 渲染层通过委托调用本方法，自身不关心着色策略。
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="category">分类名</param>
    /// <returns>WPF Color</returns>
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

    /// <summary>
    /// 应用统计列表右键弹起事件：定位被点击的行，构建上下文菜单
    /// （"颜色"=自定义该应用颜色；"更改类别"=把该应用整体归入另一分类）。
    /// </summary>
    private void AppStatsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(AppStatsList); // 鼠标相对列表控件的位置

            // 找点击的行
            var item = GetListViewItemFromPoint(AppStatsList, pos);
            if (item == null) return;

            // 从行中提取进程名
            // 找到点击的行对应的进程名
            string? processName = GetTagFromStatsRow(item);
            if (string.IsNullOrEmpty(processName)) return;

            var menu = new ContextMenu(); // 动态构建右键菜单

            // 菜单项 1：修改应用颜色
            var miColor = new MenuItem { Header = "颜色" };
            miColor.Click += (s, ev) =>
            {
                try
                {
                    var current = AppColorAllocator.GetOrAssign(processName); // 取当前色（无则自动分配）
                    var hex = PickColor(current);                             // 弹出系统取色器
                    if (hex != null) // 用户确认了新颜色
                    {
                        AppColorAllocator.SetCustom(processName, hex); // 写入自定义颜色
                        DrawAll();         // 立即用新颜色重绘所有视图
                        LoadStatsLists();  // 刷新统计列表（占比条颜色）
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
                            var selected = (Category)((Button)s2).Tag;      // 取出按钮携带的分类对象
                            RuleRepository.UpdateCategory(processName, selected.Id); // 把该进程的全部规则指向新分类
                            _classifier.ReloadRules(); // 分类器重载规则，立即生效
                            try
                            {
                                DatabaseHelper.ReclassifyAll(_classifier.Classify);
                                // 底层数据已变，使近期自动总结失效并立即补算刷新
                                AISummaryRepository.InvalidateRecent();
                                _summaryScheduler.RegenerateNow();
                                // 规则已变：同步刷新指纹，避免下次启动重复重算（2026-08-23）
                                RuleRepository.StoreFingerprint();
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

    /// <summary>
    /// 分类统计列表右键弹起事件：定位被点击的行，构建上下文菜单
    /// （"颜色"=修改该分类颜色；"查看类别"=跳到设置页的规则管理）。
    /// </summary>
    private void CategoryStatsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var item = GetListViewItemFromPoint(CategoryStatsList, e.GetPosition(CategoryStatsList));
            if (item == null) return;

            string? categoryName = GetTagFromStatsRow(item); // 行 Tag 中存的是分类名
            if (string.IsNullOrEmpty(categoryName)) return;  // 无有效分类名则忽略

            var menu = new ContextMenu();

            var miColor = new MenuItem { Header = "颜色" };
            miColor.Click += (s, ev) =>
            {
                _categoryColors.TryGetValue(categoryName, out var hex); // 取该分类当前颜色（可能为 null）
                var newHex = PickColor(hex);                            // 弹出取色器
                if (newHex != null) // 用户确认修改
                {
                    CategoryRepository.UpdateColor(categoryName, newHex); // 持久化新颜色
                    LoadCategoryColors(); // 重载分类颜色缓存
                    // 渲染器取色函数重新指向（内部读到的已是新缓存）
                    _timelineRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
                    _overviewRenderer.GetColorFunc = (proc, cat) => GetAppColor(proc, cat);
                    DrawLegend();     // 图例同步刷新
                    DrawAll();        // 重绘主视图
                    LoadStatsLists(); // 刷新统计列表
                    _statsPage?.RefreshData(); // 统计报表页若已打开也同步刷新
                }
            };
            menu.Items.Add(miColor);

            var miView = new MenuItem { Header = "查看类别" };
            miView.Click += (s, ev) => OpenSettings("rules"); // 跳到设置的规则管理分区
            menu.Items.Add(miView);

            menu.IsOpen = true;
        }
        catch (Exception ex) { MessageBox.Show($"右键菜单失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

/// <summary>
/// 弹出 WinForms 取色对话框让用户挑颜色。
/// </summary>
/// <param name="currentHex">当前颜色（十六进制），作为对话框初始值</param>
/// <returns>选中的颜色 "#RRGGBB"；用户取消返回 null</returns>
private static string? PickColor(string? currentHex = null)
{
    using var dlg = new System.Windows.Forms.ColorDialog(); // 系统取色器（用完释放）
    dlg.FullOpen = true; // 展开自定义颜色区域
    if (!string.IsNullOrEmpty(currentHex)) // 有初始色则尝试预置
    {
        try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(currentHex); } // "#RRGGBB" → Drawing.Color
        catch (Exception ex) { Logger.Error("颜色解析失败", ex); } // 非法旧值不阻断，仅记日志
    }
    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) // 用户点了确定
        return $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"; // 转回项目统一的 #RRGGBB 格式
    return null; // 用户取消
}

    /// <summary>
    /// 命中测试：根据列表内坐标找到对应的 ListViewItem（沿可视树向上查找）。
    /// </summary>
    /// <param name="list">目标 ListView</param>
    /// <param name="point">相对列表的坐标</param>
    /// <returns>命中的行容器；未命中返回 null</returns>
    private static object? GetListViewItemFromPoint(System.Windows.Controls.ListView list, System.Windows.Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject; // 先做输入命中测试拿到最内层元素
        while (hit != null && hit is not System.Windows.Controls.ListViewItem) // 逐层向上找行容器
            hit = VisualTreeHelper.GetParent(hit);
        return hit; // 可能是 ListViewItem 或 null（点到空白处）
    }

    /// <summary>从数据库重新加载分类名→颜色 的缓存字典。</summary>
    private void LoadCategoryColors()
    {
        _categoryColors = _colorHelper.Load();
    }

    /// <summary>查询某分类的颜色（缓存未命中时回退灰色）。</summary>
    private Color GetCategoryColor(string category)
    {
        return _colorHelper.GetColor(category);
    }

    /// <summary>计算时间轴画布可用宽度：容器实际宽度减去左右留白；布局未完成时用 880 兜底。</summary>
    private double GetContainerWidth()
    {
        double w = TimelineContainer.ActualWidth - 16; // 减去 Padding
        if (w <= 0) w = 880;
        return w;
    }

}
