// ============================================================================
// MainWindow.Tray.cs — 主窗口的"托盘相关行为"部分类
// 2026-08-23 方案A 重构：托盘图标与消息处理已上移到 Services.TrayHost
// （开机自启 --minimized 时窗口根本不创建，托盘必须独立于窗口存在）。
// 2026-08-25 内存优化（方案B）：点 X 不再 Hide 隐藏，而是真正 Close 销毁窗口——
// 释放 WPF 可视树与全部 UI 资源；程序驻留托盘由 ShutdownMode=OnExplicitShutdown
// 保证。销毁前把查看日期/缩放级别/窗口几何持久化，重建窗口时恢复，保证
// 除"从托盘打开变慢"外的用户体验不变。
// 本文件职责：
//   1) ShowFromTray()：从托盘显示（窗口由 TrayHost 延迟新建，本方法只负责展示）；
//   2) SaveWindowState()/RestoreWindowState()：窗口状态持久化与恢复；
//   3) OnClosing：点 X → 保存状态后放行关闭（销毁）；真正退出时停后台服务。
// ============================================================================
using System;
using System.Globalization;
using System.Windows;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity;

public partial class MainWindow
{
    /// <summary>从托盘恢复主窗口：显示并抢焦点置前（窗口为新建实例，几何/最大化状态已在构造时恢复）。</summary>
    public void ShowFromTray()
    {
        // 2026-08-25 方案B：窗口关闭即销毁，这里拿到的永远是新建实例（构造时已恢复状态），
        // 无需额外的重载分支；也不重置 WindowState，保留构造时恢复的最大化状态
        Show();                          // 以构造时状态显示窗口
        Activate();                      // 激活窗口并将其带到前台
    }

    // ==================== 窗口状态持久化（2026-08-25 方案B补偿） ====================

    /// <summary>
    /// 保存窗口状态：查看日期、时间轴缩放级别、窗口位置/大小、最大化标志。
    /// 关闭销毁前调用，重建窗口时由 RestoreWindowState 找回，保持体验一致。
    /// 使用 SetMany 单事务批量写入，避免逐个 Set 的连接开销。
    /// </summary>
    private void SaveWindowState()
    {
        try
        {
            var items = new System.Collections.Generic.KeyValuePair<string, string>[]
            {
                new("Ui_CurrentDate", _currentDate.ToString("yyyy-MM-dd")),
                new("Ui_VisibleSeconds", ((long)_visibleSeconds).ToString(CultureInfo.InvariantCulture)),
                new("Ui_ViewStartSeconds", ((long)_viewStartSeconds).ToString(CultureInfo.InvariantCulture)),
                new("Ui_WindowLeft", ((int)Left).ToString(CultureInfo.InvariantCulture)),
                new("Ui_WindowTop", ((int)Top).ToString(CultureInfo.InvariantCulture)),
                new("Ui_WindowWidth", ((int)ActualWidth).ToString(CultureInfo.InvariantCulture)),
                new("Ui_WindowHeight", ((int)ActualHeight).ToString(CultureInfo.InvariantCulture)),
                new("Ui_WindowMaximized", WindowState == WindowState.Maximized ? "1" : "0")
            };
            SettingsRepository.SetMany(items);
        }
        catch (Exception ex)
        {
            Logger.Error("保存窗口状态失败", ex);
        }
    }

    /// <summary>
    /// 恢复上次的窗口状态。构造函数中调用（须在 LoadDateData 之前，日期才生效）。
    /// 位置/大小做了最小化与屏幕范围校验，防止多显示器变化后窗口跑到屏幕外。
    /// </summary>
    private void RestoreWindowState()
    {
        try
        {
            // 查看日期：非法/缺失回退今天
            var dateStr = SettingsRepository.Get("Ui_CurrentDate", "");
            if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d) && d <= DateTime.Today)
                _currentDate = d;

            // 时间轴缩放级别与视口起点：钳制到合法范围
            if (double.TryParse(SettingsRepository.Get("Ui_VisibleSeconds", ""),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var vs))
                _visibleSeconds = Math.Clamp(vs, MinVisibleSeconds, MaxVisibleSeconds);
            if (double.TryParse(SettingsRepository.Get("Ui_ViewStartSeconds", ""),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var vss))
                _viewStartSeconds = Math.Clamp(vss, 0, Math.Max(0, MaxVisibleSeconds - _visibleSeconds));

            // 窗口几何：整数值且落在虚拟屏幕内才恢复（多显示器拔插后防止窗口丢失）
            int vLeft = (int)SystemParameters.VirtualScreenLeft;
            int vTop = (int)SystemParameters.VirtualScreenTop;
            int vWidth = (int)SystemParameters.VirtualScreenWidth;
            int vHeight = (int)SystemParameters.VirtualScreenHeight;

            if (int.TryParse(SettingsRepository.Get("Ui_WindowLeft", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) &&
                int.TryParse(SettingsRepository.Get("Ui_WindowTop", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) &&
                int.TryParse(SettingsRepository.Get("Ui_WindowWidth", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
                int.TryParse(SettingsRepository.Get("Ui_WindowHeight", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                // 校验：窗口至少有 400x300，且主体落在虚拟屏幕范围内
                if (width >= 400 && height >= 300 &&
                    left + 100 >= vLeft && left <= vLeft + vWidth - 100 &&
                    top >= vTop && top <= vTop + vHeight - 40)
                {
                    Left = left; Top = top; Width = width; Height = height;
                }
            }

            // 最大化标志：Show 时生效
            if (SettingsRepository.Get("Ui_WindowMaximized", "0") == "1")
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            Logger.Error("恢复窗口状态失败", ex);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭按钮（非强制退出）→ 保存状态并放行关闭：窗口真正销毁，程序驻留托盘。
        // 与旧的 Hide 方案相比：WPF 可视树/统计页/数据缓存随窗口一起释放，后台内存更低；
        // 代价是从托盘打开需要重建窗口（约 0.5~1s），由 RestoreWindowState 找回体验。
        if (!_forceClose && SettingsRepository.Get("MinimizeToTray", "true") == "true")
        {
            SaveWindowState();   // 持久化日期/缩放/几何，供重建时恢复
            base.OnClosing(e);   // 不取消：继续关闭销毁流程（TrayHost.Closed 里置空并回收）
            return;
        }

        // —— 真正退出的清理流程：按依赖顺序停掉全部后台服务 ——
        // （这些单例同时被 AppServices 管理，此处提前停止以尽快落库最后一条活动）
        _engine.Stop();           // 停止活动追踪轮询（会落库最后一条未完结活动）
        _screenshotService.Stop();// 停止定时截图服务
        _summaryScheduler.Stop(); // 停止日/周/月 AI 总结调度器
        base.OnClosing(e);        // 继续默认关闭流程（App.OnExit 兜底其余清理）
    }
}
