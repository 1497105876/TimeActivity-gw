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
// MainWindow.Timeline.cs — 主窗口的"时间轴视图与交互"部分类
// 职责：
//   1) DrawAll：统一重绘上方时间轴、刻度与下方 24 小时概览条，并更新缩放倍数显示；
//   2) 时间轴滚轮缩放（以鼠标位置为锚点）；
//   3) 概览条拖拽平移可视窗口；
//   4) 鼠标悬停时的活动命中检测与详情浮窗（含截图预览）；
//   5) 分类图例的动态构建。
// 坐标系约定：时间轴/概览均以"当天秒数 0~86400"为时间域，宽度像素线性映射。
// 协作对象：TimelineRenderer/OverviewRenderer(绘制)、ScreenshotService(截图)、
//           CategoryColorHelper(取色)。
// ============================================================================
public partial class MainWindow
{
    /// <summary>
    /// 重绘全部可视化组件：时间轴主体、顶部刻度、概览条及其刻度、缩放倍数文本。
    /// 数据来源为 _cachedActivities；视口参数为 _viewStartSeconds/_visibleSeconds。
    /// </summary>
    private void DrawAll()
    {
        double w = GetContainerWidth(); // 计算当前可用画布宽度

        // 上方时间轴
        TopScaleCanvas.Width = w;     // 刻度条与主体保持同宽
        MainTimelineCanvas.Width = w; // 保证渲染器按同一坐标系绘制
        // 绘制活动区间；若统计列表有勾选(高亮)则传入高亮集合用于淡化未选中项
        _timelineRenderer.DrawActivities(MainTimelineCanvas, w, TimelineHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds,
            ActiveAppHighlights.Count > 0 ? ActiveAppHighlights : null,
            ActiveCategoryHighlights.Count > 0 ? ActiveCategoryHighlights : null);
        _timelineRenderer.DrawScale(TopScaleCanvas, w, _viewStartSeconds, _visibleSeconds); // 按当前视口绘制时间刻度

        // 下方概览条
        OverviewCanvas.Width = w;      // 概览条同宽
        OverviewScaleCanvas.Width = w; // 概览刻度同宽
        _overviewRenderer.Draw(OverviewCanvas, w, OverviewHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds); // 全天概览+当前视口窗口指示
        _overviewRenderer.DrawScale(OverviewScaleCanvas, w); // 概览条的整日刻度

        // 更新缩放显示
        double zoomLevel = 86400.0 / _visibleSeconds; // 一天总秒数 / 可见秒数 = 放大倍数
        ZoomText.Text = $"缩放：{zoomLevel:F1}x";
    }

    /// <summary>
    /// 时间轴滚轮缩放：上滚放大、下滚缩小，并以鼠标所在时刻为锚点，
    /// 保证缩放前后鼠标指向的时刻在屏幕上的位置不变。
    /// </summary>
    private void MainTimelineCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 鼠标在时间轴上的 X 坐标
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        // 鼠标 X 坐标对应的时间（秒）
        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 滚轮上滚放大（×0.8），下滚缩小（×1.25）
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        double newVisible = Math.Clamp(_visibleSeconds * factor, MinVisibleSeconds, MaxVisibleSeconds); // 限制在最小/最大可见范围
        if (newVisible == _visibleSeconds) { e.Handled = true; return; } // 已到缩放极限则不再重绘

        _visibleSeconds = newVisible; // 应用新的可视窗口大小

        // 调整起始位置使鼠标处的时间保持不变
        _viewStartSeconds = mouseTime - (mouseX / width) * _visibleSeconds;
        _viewStartSeconds = Math.Clamp(_viewStartSeconds, 0, MaxVisibleSeconds - _visibleSeconds); // 起点不能越界

        DrawAll();        // 立即重绘反映新视口
        e.Handled = true; // 标记已处理，阻止滚动冒泡到父容器
    }

    /// <summary>
    /// 概览条按下鼠标：开始拖拽平移，记录起点并捕获鼠标（移出控件也能继续收到事件）。
    /// </summary>
    private void OverviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = true;                        // 进入拖拽状态
        _dragStartX = e.GetPosition(OverviewCanvas).X;   // 记录按下时的 X 像素
        _dragStartViewStart = _viewStartSeconds;         // 记录拖拽前的视口起点
        OverviewCanvas.CaptureMouse();                   // 捕获鼠标
    }

    /// <summary>
    /// 概览条拖拽中：把像素位移换算成秒数位移，实时更新视口起点并重绘。
    /// </summary>
    private void OverviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_overviewDragging) return; // 未处于拖拽状态直接忽略
        double width = GetContainerWidth();
        double curX = e.GetPosition(OverviewCanvas).X;
        double deltaSeconds = ((curX - _dragStartX) / width) * 86400; // 像素差 → 秒差（概览覆盖全天86400s）
        _viewStartSeconds = Math.Clamp(_dragStartViewStart + deltaSeconds, 0, MaxVisibleSeconds - _visibleSeconds); // 平移并防越界
        DrawAll(); // 每次移动都重绘（轻量绘制，可接受）
    }

    /// <summary>概览条松开鼠标：结束拖拽并释放鼠标捕获。</summary>
    private void OverviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = false;
        OverviewCanvas.ReleaseMouseCapture();
    }

    /// <summary>
    /// 时间轴鼠标移动：做活动命中检测并驱动详情浮窗（Popup）跟随显示，
    /// 命中时展示分类/时段/进程/标题/截图，未命中时仅显示时刻。
    /// </summary>
    private void MainTimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        // 鼠标 X 坐标对应的时间（秒）
        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 查找鼠标时间落在哪个活动区间
        ActivityRecord? hit = null; // 命中的活动记录
        foreach (var act in _cachedActivities)
        {
            if (act.IsIdle) continue; // 空闲段不参与悬停提示
            // 用绝对时间比较，不用 TimeOfDay（跨午夜活动EndTime.TimeOfDay会归零）
            // 将活动起止转为当天秒数，跨午夜活动endSec会小于startSec，需特殊处理
            double startSec = act.StartTime.TimeOfDay.TotalSeconds;
            double endSec = act.EndTime.TimeOfDay.TotalSeconds;
            // 跨午夜活动（endSec < startSec），endSec 加一天秒数
            if (endSec < startSec) endSec += 86400;
            // mouseTime 也可能小于 startSec（如凌晨0点后看前一天23点开始的活踯）
            double mt = mouseTime;
            if (mt < startSec) mt += 86400;
            if (mt >= startSec && mt < endSec)
            {
                hit = act;
                break;
            }
        }

        if (!_popupOpen) // Popup 尚未打开则打开（避免重复设置 IsOpen 引发闪烁）
        {
            DetailPopup.IsOpen = true;
            _popupOpen = true;
        }

        // 每次移动都更新 Popup 位置 — 相对于 MainTimelineCanvas
        var canvasPos = e.GetPosition(MainTimelineCanvas);
        DetailPopup.HorizontalOffset = canvasPos.X + 14; // 右偏 14px 避免遮挡鼠标指针
        DetailPopup.VerticalOffset = canvasPos.Y + 18;   // 下偏 18px

        if (hit != null)
        {
            // 命中活动：显示颜色块、分类、时间、进程名、标题、截图
            PopupColor.Visibility = Visibility.Visible;
            PopupCategory.Visibility = Visibility.Visible;
            PopupProcess.Visibility = Visibility.Visible;
            PopupColor.Fill = new SolidColorBrush(GetCategoryColor(hit.Category));
            PopupCategory.Text = $"{hit.Category}  ·  {TimeFormatHelper.Format(hit.Duration)}";
            PopupTime.Text = $"{hit.StartTime:HH:mm:ss} → {hit.EndTime:HH:mm:ss}";
            PopupProcess.Text = hit.ProcessName;
            PopupTitle.Text = hit.WindowTitle;
            PopupTitle.Visibility = string.IsNullOrEmpty(hit.WindowTitle) ? Visibility.Collapsed : Visibility.Visible;

            // 用活动的开始~结束时间查截图（截图必须在活动期间内拍的）
            var screenshotPath = ScreenshotService.GetScreenshotForTime(hit.StartTime, hit.EndTime);
            if (screenshotPath != null) // 找到匹配截图
            {
                if (_lastScreenshotPath != screenshotPath) // 与上次不同才重新加载位图（避免重复 IO）
                {
                    PopupScreenshot.Source = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(screenshotPath));
                    _lastScreenshotPath = screenshotPath;
                }
                PopupScreenshot.Visibility = Visibility.Visible;
            }
            else
            {
                // 没有截图时清空缓存和 Source，防止残留旧图
                _lastScreenshotPath = null;
                PopupScreenshot.Source = null;
                PopupScreenshot.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            // 没命中活动时只显示时间1
            TimeSpan ts = TimeSpan.FromSeconds(mouseTime);
            PopupColor.Visibility = Visibility.Collapsed;
            PopupCategory.Visibility = Visibility.Collapsed;
            PopupProcess.Visibility = Visibility.Collapsed;
            PopupTitle.Visibility = Visibility.Collapsed;
            PopupScreenshot.Visibility = Visibility.Collapsed;
            PopupTime.Text = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    /// <summary>
    /// 鼠标离开时间轴：关闭浮窗并清空截图相关状态，防止残留旧图。
    /// </summary>
    private void MainTimelineCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        DetailPopup.IsOpen = false; // 关闭详情浮窗
        _popupOpen = false;         // 同步打开标志
        _lastScreenshotPath = null; // 清空截图路径缓存
        PopupScreenshot.Source = null;            // 释放位图引用
        PopupScreenshot.Visibility = Visibility.Collapsed; // 隐藏截图区
    }

    /// <summary>
    /// 绘制分类图例：按缓存字典逐项生成"色块+分类名"的水平小面板。
    /// 每次全量重建（数量少，代价可忽略）。
    /// </summary>
    private void DrawLegend()
    {
        LegendPanel.Children.Clear(); // 先清空旧图例
        foreach (var kvp in _categoryColors) // 遍历 分类名→颜色串
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) }; // 一项一个横排容器
            var rect = new Rectangle // 色块
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(CategoryColorHelper.ParseHex(kvp.Value)), // 解析颜色字符串
                RadiusX = 2, RadiusY = 2, // 圆角
                VerticalAlignment = VerticalAlignment.Center
            };
            var text = new TextBlock // 分类名文字
            {
                Text = kvp.Key, FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0), // 与色块留 4px 间距
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(rect);   // 加入色块
            panel.Children.Add(text);   // 加入文字
            LegendPanel.Children.Add(panel); // 挂到图例容器
        }
    }

}
