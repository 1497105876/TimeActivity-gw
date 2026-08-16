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
    private void DrawAll()
    {
        double w = GetContainerWidth();

        // 上方时间轴
        TopScaleCanvas.Width = w;
        MainTimelineCanvas.Width = w;
        _timelineRenderer.DrawActivities(MainTimelineCanvas, w, TimelineHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds,
            ActiveAppHighlights.Count > 0 ? ActiveAppHighlights : null,
            ActiveCategoryHighlights.Count > 0 ? ActiveCategoryHighlights : null);
        _timelineRenderer.DrawScale(TopScaleCanvas, w, _viewStartSeconds, _visibleSeconds);

        // 下方概览条
        OverviewCanvas.Width = w;
        OverviewScaleCanvas.Width = w;
        _overviewRenderer.Draw(OverviewCanvas, w, OverviewHeight, _cachedActivities, _viewStartSeconds, _visibleSeconds);
        _overviewRenderer.DrawScale(OverviewScaleCanvas, w);

        // 更新缩放显示
        double zoomLevel = 86400.0 / _visibleSeconds;
        ZoomText.Text = $"缩放：{zoomLevel:F1}x";
    }

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
        double newVisible = Math.Clamp(_visibleSeconds * factor, MinVisibleSeconds, MaxVisibleSeconds);
        if (newVisible == _visibleSeconds) { e.Handled = true; return; }

        _visibleSeconds = newVisible;

        // 调整起始位置使鼠标处的时间保持不变
        _viewStartSeconds = mouseTime - (mouseX / width) * _visibleSeconds;
        _viewStartSeconds = Math.Clamp(_viewStartSeconds, 0, MaxVisibleSeconds - _visibleSeconds);

        DrawAll();
        e.Handled = true;
    }

    private void OverviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = true;
        _dragStartX = e.GetPosition(OverviewCanvas).X;
        _dragStartViewStart = _viewStartSeconds;
        OverviewCanvas.CaptureMouse();
    }

    private void OverviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_overviewDragging) return;
        double width = GetContainerWidth();
        double curX = e.GetPosition(OverviewCanvas).X;
        double deltaSeconds = ((curX - _dragStartX) / width) * 86400;
        _viewStartSeconds = Math.Clamp(_dragStartViewStart + deltaSeconds, 0, MaxVisibleSeconds - _visibleSeconds);
        DrawAll();
    }

    private void OverviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _overviewDragging = false;
        OverviewCanvas.ReleaseMouseCapture();
    }

    private void MainTimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        double mouseX = e.GetPosition(MainTimelineCanvas).X;
        double width = GetContainerWidth();
        if (width <= 0) return;

        // 鼠标 X 坐标对应的时间（秒）
        double mouseTime = _viewStartSeconds + (mouseX / width) * _visibleSeconds;

        // 查找鼠标时间落在哪个活动区间
        ActivityRecord? hit = null;
        foreach (var act in _cachedActivities)
        {
            if (act.IsIdle) continue;
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

        if (!_popupOpen)
        {
            DetailPopup.IsOpen = true;
            _popupOpen = true;
        }

        // 每次移动都更新 Popup 位置 — 相对于 MainTimelineCanvas
        var canvasPos = e.GetPosition(MainTimelineCanvas);
        DetailPopup.HorizontalOffset = canvasPos.X + 14;
        DetailPopup.VerticalOffset = canvasPos.Y + 18;

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
            if (screenshotPath != null)
            {
                if (_lastScreenshotPath != screenshotPath)
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

    private void MainTimelineCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        DetailPopup.IsOpen = false;
        _popupOpen = false;
        _lastScreenshotPath = null;
        PopupScreenshot.Source = null;
        PopupScreenshot.Visibility = Visibility.Collapsed;
    }

    private void DrawLegend()
    {
        LegendPanel.Children.Clear();
        foreach (var kvp in _categoryColors)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            var rect = new Rectangle
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(CategoryColorHelper.ParseHex(kvp.Value)),
                RadiusX = 2, RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            var text = new TextBlock
            {
                Text = kvp.Key, FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(rect);
            panel.Children.Add(text);
            LegendPanel.Children.Add(panel);
        }
    }

}
