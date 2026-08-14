using System;
using System.Threading;
using System.Threading.Tasks;
using TimeActivity.Models;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity.Services;

/// <summary>
/// 追踪引擎 — 后台定时采样当前前台窗口，记录用户在用什么软件
/// 切换窗口或空闲时，把活动记录存入数据库
/// </summary>
public class TrackingEngine
{
    private readonly ActivityClassifier _classifier;
    private CancellationTokenSource? _cts;
    private ActivityRecord? _currentActivity;

    // 采样间隔（秒）
    public int PollIntervalSeconds { get; set; } = 3;

    // 空闲阈值（秒）— 超过这个时间没操作就标记为空闲
    public int IdleThresholdSeconds { get; set; } = 300; // 5分钟

    // 当一条活动记录结束时触发（切换软件或空闲）
    public event Action<ActivityRecord>? OnActivityRecorded;

    // 当切换应用时触发（用于截图）
    public event Action? OnAppSwitched;

    // 当前状态变化时触发
    public event Action<string, string, string>? OnStatusChanged;

    // 是否正在运行
    public bool IsRunning => _cts != null;

    private string _lastProcessName = string.Empty;
    private string _lastWindowTitle = string.Empty;

    public TrackingEngine(ActivityClassifier classifier)
    {
        _classifier = classifier;
    }

    /// <summary>
    /// 启动追踪
    /// </summary>
    public void Start()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoop(_cts.Token));
    }

    /// <summary>
    /// 停止追踪
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        // 把当前活动收尾
        FinishCurrentActivity();
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Poll();
            try
            {
                int delayMs = Math.Clamp(PollIntervalSeconds, 1, 3600) * 1000;
                await Task.Delay(delayMs, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void Poll()
    {
        // 检查空闲
        int idleSeconds = Win32Api.GetIdleSeconds();

        if (idleSeconds >= IdleThresholdSeconds)
        {
            // 用户离开了 — 结束当前活动
            if (_currentActivity != null && !_currentActivity.IsIdle)
            {
                FinishCurrentActivity();
            }

            // 记录空闲时间
            if (_currentActivity == null || _currentActivity.ProcessName != "(空闲)")
            {
                _currentActivity = new ActivityRecord
                {
                    ProcessName = "(空闲)",
                    WindowTitle = "用户离开",
                    Category = "空闲",
                    StartTime = DateTime.Now,
                    IsIdle = true
                };
                OnStatusChanged?.Invoke("(空闲)", "用户离开", "空闲");
            }
            return;
        }

        // 获取当前前台窗口
        IntPtr hWnd = Win32Api.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return;

        string processName = Win32Api.GetProcessName(hWnd);
        string windowTitle = Win32Api.GetWindowTitle(hWnd);

        // 窗口标题为空时用进程名兜底（全屏游戏/DirectX 独占可能标题为空）
        if (string.IsNullOrEmpty(windowTitle))
            windowTitle = processName;

        string category = _classifier.Classify(processName, windowTitle);

        // 状态变化通知
        OnStatusChanged?.Invoke(processName, windowTitle, category);

        // 如果进程名或标题变了，说明切换了软件 — 结束旧活动，开始新活动
        if (processName != _lastProcessName || windowTitle != _lastWindowTitle)
        {
            OnAppSwitched?.Invoke();
            FinishCurrentActivity();

            _currentActivity = new ActivityRecord
            {
                ProcessName = processName,
                WindowTitle = windowTitle,
                Category = category,
                StartTime = DateTime.Now,
                IsIdle = false
            };

            _lastProcessName = processName;
            _lastWindowTitle = windowTitle;
        }
        // 否则就是同一个软件继续用，什么都不做，等切换时再算时长
    }

    private void FinishCurrentActivity()
    {
        if (_currentActivity == null) return;

        _currentActivity.EndTime = DateTime.Now;
        _currentActivity.Duration = (int)(_currentActivity.EndTime - _currentActivity.StartTime).TotalSeconds;

        // 只记录超过 1 秒的活动，过滤掉一闪而过的窗口
        if (_currentActivity.Duration >= 1)
        {
            // 存入数据库
            try
            {
                _currentActivity.Id = ActivityRepository.Insert(_currentActivity);
            }
            catch (Exception ex)
            {
                Logger.Error("活动写入数据库失败", ex);
            }

            OnActivityRecorded?.Invoke(_currentActivity);
        }

        _currentActivity = null;
    }
}
