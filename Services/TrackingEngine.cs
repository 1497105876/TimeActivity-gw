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
    // 分类器，给活动归类别用的
    private readonly ActivityClassifier _classifier;
    // 取消令牌，控制轮询循环的退出
    private CancellationTokenSource? _cts;
    // 当前正在进行的活动记录
    private ActivityRecord? _currentActivity;
    // 锁，防止 Stop 和 PollLoop 同时操作 _currentActivity
    private readonly object _lock = new();

    // 采样间隔（秒），默认 3 秒轮一次
    public int PollIntervalSeconds { get; set; } = 3;

    // 空闲阈值（秒）— 超过这个时间没操作就标记为空闲，默认 5 分钟
    public int IdleThresholdSeconds { get; set; } = 300;

    // 当一条活动记录结束时触发（切换软件或空闲），外部可以用来更新 UI
    public event Action<ActivityRecord>? OnActivityRecorded;

    // 当切换应用时触发（截图服务监听这个事件）
    public event Action? OnAppSwitched;

    // 当前状态变化时触发（进程名、窗口标题、类别），UI 用来显示实时状态
    public event Action<string, string, string>? OnStatusChanged;

    // 是否正在运行
    public bool IsRunning => _cts != null;

    // 记住上一次的进程名和窗口标题，用来判断是否切换了软件
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

        // 加锁防止和 PollLoop 竞态同时操作 _currentActivity
        lock (_lock)
        {
            FinishCurrentActivity();
        }
    }

    /// <summary>
    /// 轮询循环：每隔 PollIntervalSeconds 秒采样一次当前前台窗口。
    /// </summary>
    /// <param name="token">取消令牌</param>
    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                // 单次采样异常（如 Win32 P/Invoke 偶发失败）不应让整个轮询循环停摆，
                // 记日志后跳过本次，继续下一轮采样，保证追踪引擎持续运行。
                Logger.Error("轮询采样异常，已跳过本次采样", ex);
            }
            try
            {
                // 限制间隔在 1~3600 秒之间
                int delayMs = Math.Clamp(PollIntervalSeconds, 1, 3600) * 1000;
                await Task.Delay(delayMs, token);
            }
            catch (TaskCanceledException)
            {
                // 被取消时退出循环
                break;
            }
        }
    }

    /// <summary>
    /// 单次轮询：检查空闲 → 获取前台窗口 → 判断是否切换了软件 → 结束旧活动/开始新活动。
    /// </summary>
    private void Poll()
    {
        // 加锁防止和 Stop 竞态
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            PollInternal();
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>
    /// 单次轮询内部实现。
    /// </summary>
    private void PollInternal()
    {
        // 先检查用户是否空闲（通过 Win32 API 获取最后一次输入的时间）
        int idleSeconds = Win32Api.GetIdleSeconds();

        if (idleSeconds >= IdleThresholdSeconds)
        {
            // 用户离开了 — 结束当前活动
            if (_currentActivity != null && !_currentActivity.IsIdle)
            {
                FinishCurrentActivity();
            }

            // 开始记录空闲时间
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

        // 用户回来了 — 如果当前是空闲状态，强制结束空闲开始新活动
        if (_currentActivity != null && _currentActivity.IsIdle)
        {
            FinishCurrentActivity();
            // 清空 last 记录，强制下面的切换逻辑触发开始新活动
            _lastProcessName = "";
            _lastWindowTitle = "";
        }

        // 获取当前前台窗口的进程名和标题（通过 Win32 API）
        IntPtr hWnd = Win32Api.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return;

        string processName = Win32Api.GetProcessName(hWnd);
        string windowTitle = Win32Api.GetWindowTitle(hWnd);

        // 窗口标题为空时用进程名兜底（全屏游戏/DirectX 独占可能拿不到标题）
        if (string.IsNullOrEmpty(windowTitle))
            windowTitle = processName;

        // 用分类器给当前活动归类
        string category = _classifier.Classify(processName, windowTitle);

        // 通知 UI 更新实时状态
        OnStatusChanged?.Invoke(processName, windowTitle, category);

        // 进程名或标题变了 = 切换了软件 — 结束旧活动，开始新活动
        if (processName != _lastProcessName || windowTitle != _lastWindowTitle)
        {
            OnAppSwitched?.Invoke();  // 通知截图服务
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

    /// <summary>
    /// 结束当前活动：计算时长，存入数据库，触发回调。只记录超过 1 秒的活动。
    /// </summary>
    private void FinishCurrentActivity()
    {
        if (_currentActivity == null) return;

        _currentActivity.EndTime = DateTime.Now;
        _currentActivity.Duration = (int)(_currentActivity.EndTime - _currentActivity.StartTime).TotalSeconds;

        // 过滤掉一闪而过的窗口（不足 1 秒的不记录）
        if (_currentActivity.Duration >= 1)
        {
            try
            {
                _currentActivity.Id = ActivityRepository.Insert(_currentActivity);
            }
            catch (Exception ex)
            {
                Logger.Error("活动写入数据库失败", ex);
            }

            // 通知外部（UI 更新）
            OnActivityRecorded?.Invoke(_currentActivity);
        }

        _currentActivity = null;
    }
}
