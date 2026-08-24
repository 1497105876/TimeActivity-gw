// ============================================================================
// MainWindow.Tray.cs — 主窗口的"托盘相关行为"部分类
// 2026-08-23 方案A 重构：托盘图标与消息处理已上移到 Services.TrayHost
// （开机自启 --minimized 时窗口根本不创建，托盘必须独立于窗口存在）。
// 本文件仅保留：
//   1) 从托盘恢复窗口的 ShowFromTray()（供 TrayHost 调用）；
//   2) OnClosing：点 X 默认隐藏到托盘；真正退出时停后台服务（幂等，
//      App.OnExit 还会再统一兜底清理）。
// ============================================================================
using System;
using System.Windows;

namespace TimeActivity;

public partial class MainWindow
{
    /// <summary>从托盘恢复主窗口：显示、还原尺寸并抢焦点置前。</summary>
    public void ShowFromTray()
    {
        Show();                          // 撤销 Hide() 的隐藏状态
        WindowState = WindowState.Normal;// 若之前是最小化则还原为普通大小
        Activate();                      // 激活窗口并将其带到前台
    }

    /// <summary>关闭拦截：点 X 默认最小化到托盘；强制退出时按依赖顺序停止后台服务。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭按钮 → 最小化到托盘（除非是强制退出）
        if (!_forceClose && Data.SettingsRepository.Get("MinimizeToTray", "true") == "true")
        {
            // 取消真实关闭，仅隐藏窗口驻留托盘
            e.Cancel = true;
            // 隐藏窗口（任务栏消失，托盘图标仍在）
            Hide();
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
