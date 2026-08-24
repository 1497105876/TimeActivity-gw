// ============================================================================
// TrackingEngine.cs — 活动追踪引擎（核心采样循环）
// 职责：后台定时采样前台窗口进程名/标题，识别应用切换与用户空闲；
//       活动结束时计算时长写入数据库，并通过事件通知 UI 与截图服务。
// 并发模型：PollLoop 后台任务与 Stop 通过 _lock 互斥；事件回调一律收集后在锁外触发。
// ============================================================================
// —— .NET 基础库：集合类型、线程/取消令牌、异步任务 ——
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
// —— 项目内模块：数据模型 / 数据仓储 / 服务层（Win32Api、Logger 等） ——
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
    // ==================== 字段 ====================

    // 分类器，给活动归类别用的
    private readonly ActivityClassifier _classifier;
    // 取消令牌，控制轮询循环的退出
    // 注意：非 volatile 且 Start/Stop 未加锁，极端并发下存在轻微竞态（实际仅 UI 单线程调用）
    private CancellationTokenSource? _cts;
    // 当前正在进行的活动记录
    // 只允许在 _lock 保护内读写，保证轮询与停止互不踩踏
    private ActivityRecord? _currentActivity;
    // 锁，防止 Stop 和 PollLoop 同时操作 _currentActivity
    private readonly object _lock = new();

    // ==================== 公开配置与事件 ====================

    /// <summary>采样间隔（秒），默认 3 秒轮一次；运行中修改会在下一次延迟计算时生效。</summary>
    // 采样间隔（秒），默认 3 秒轮一次
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>空闲阈值（秒）：超过该时长无键鼠输入即判定用户离开，默认 5 分钟。</summary>
    // 空闲阈值（秒）— 超过这个时间没操作就标记为空闲，默认 5 分钟
    public int IdleThresholdSeconds { get; set; } = 300;

    /// <summary>当一条活动记录结束时触发（参数为已落库的记录），外部可用来更新 UI。</summary>
    // 当一条活动记录结束时触发（切换软件或空闲），外部可以用来更新 UI
    public event Action<ActivityRecord>? OnActivityRecorded;

    /// <summary>当切换应用时触发，截图服务监听此事件实现"切换即截屏"。</summary>
    // 当切换应用时触发（截图服务监听这个事件）
    public event Action? OnAppSwitched;

    /// <summary>实时状态变化时触发（进程名、窗口标题、类别），UI 用于展示当前正在使用的应用。</summary>
    // 当前状态变化时触发（进程名、窗口标题、类别），UI 用来显示实时状态
    public event Action<string, string, string>? OnStatusChanged;

    /// <summary>是否正在运行：以取消令牌源是否存在为准。</summary>
    // 是否正在运行
    public bool IsRunning => _cts != null;

    // 记住上一次的进程名和窗口标题，用来判断是否切换了软件
    private string _lastProcessName = string.Empty;
    private string _lastWindowTitle = string.Empty;

    // ==================== 生命周期 ====================

    /// <summary>
    /// 构造函数：注入分类器依赖（分类规则由分类器自己管理）。
    /// </summary>
    public TrackingEngine(ActivityClassifier classifier)
    {
        _classifier = classifier;
    }

    /// <summary>
    /// 启动追踪
    /// </summary>
    public void Start()
    {
        // 幂等保护：已在运行就直接返回，避免重复起轮询循环
        if (_cts != null) return;

        // 新建取消令牌源，供 Stop 时通知轮询循环退出
        _cts = new CancellationTokenSource();
        // fire-and-forget 启动后台轮询任务：故意丢弃 Task 引用；
        // 循环内部已自行捕获全部异常，不会产生未观察异常导致进程崩溃
        _ = Task.Run(() => PollLoop(_cts.Token));
    }

    /// <summary>
    /// 停止追踪
    /// </summary>
    public void Stop()
    {
        // 先发取消信号，让轮询循环尽快从 Task.Delay 中醒来并退出
        _cts?.Cancel();
        // 立即置空令牌源，IsRunning 马上变 false；旧实例交给 GC
        _cts = null;

        // 加锁防止和 PollLoop 竞态同时操作 _currentActivity
        // 回调列表：先在锁内收集需要通知外部的动作，释放锁之后再统一触发
        List<Action> callbacks;
        lock (_lock)
        {
            // 收集容器传给收尾方法，由它填充
            callbacks = new List<Action>();
            // 结束尚未落库的进行中活动（若有），把通知动作先收进列表
            FinishCurrentActivity(callbacks);
        }
        // 锁外触发事件，避免回调（UI/截图）在 Stop 持有的锁内执行
        foreach (var cb in callbacks) cb();
    }

    // ==================== 轮询核心 ====================

    /// <summary>
    /// 轮询循环：每隔 PollIntervalSeconds 秒采样一次当前前台窗口。
    /// </summary>
    /// <param name="token">取消令牌</param>
    private async Task PollLoop(CancellationToken token)
    {
        // 只要未被取消就持续轮询
        while (!token.IsCancellationRequested)
        {
            // 执行一次采样
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
                // 防止设置被改成 0/负数造成忙转，或超大值造成"假死"
                int delayMs = Math.Clamp(PollIntervalSeconds, 1, 3600) * 1000;
                // 异步等待下一个采样点；Stop 取消时这里会抛 TaskCanceledException
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
    /// 锁内只做状态变更与落库；需要通知外部的事件先收集起来，释放锁之后再触发，
    /// 避免同步回调（尤其是截图这种耗时操作）长时间占用锁，导致轮询循环与 Stop 被拖住。
    /// </summary>
    private void Poll()
    {
        // 加锁防止和 Stop 竞态
        // 用 TryEnter 而非 Enter：拿不到锁说明 Stop 正持有锁，直接放弃本轮采样，
        // 不排队等待，保证 Stop 能尽快完成
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            // 本轮要触发的回调容器
            var postLockCallbacks = new List<Action>();
            // 真正的采样逻辑（锁内），把通知动作收集进列表
            PollInternal(postLockCallbacks);
            // 锁外触发事件：截图/UI 更新不再占用 _lock
            foreach (var cb in postLockCallbacks) cb();
        }
        finally
        {
            // finally 保证任何路径（含异常）都释放锁，否则轮询/停止将永久卡死
            Monitor.Exit(_lock);
        }
    }

    /// <summary>
    /// 单次轮询内部实现。需要通知外部的事件收集到 callbacks，由调用方在释放锁之后触发。
    /// </summary>
    /// <param name="callbacks">收集锁外要触发的事件回调</param>
    private void PollInternal(List<Action> callbacks)
    {
        // 先检查用户是否空闲（通过 Win32 API 获取最后一次输入的时间）
        int idleSeconds = Win32Api.GetIdleSeconds();

        // 空闲判定：距上次键鼠输入已达阈值 → 进入"用户离开"分支
        if (idleSeconds >= IdleThresholdSeconds)
        {
            // 用户离开了 — 结束当前活动
            // 只收尾"非空闲"的活动，避免对空闲段重复结算
            if (_currentActivity != null && !_currentActivity.IsIdle)
            {
                FinishCurrentActivity(callbacks);
            }

            // 开始记录空闲时间
            // 条件：当前没有活动，或当前活动还不是空闲段 → 新建一条空闲活动
            if (_currentActivity == null || _currentActivity.ProcessName != "(空闲)")
            {
                _currentActivity = new ActivityRecord
                {
                    // 进程名用魔法字符串"(空闲)"标识空闲段
                    ProcessName = "(空闲)",
                    WindowTitle = "用户离开",
                    Category = "空闲",
                    StartTime = DateTime.Now,
                    // IsIdle 标记用于回来时识别并收尾这段空闲记录
                    IsIdle = true
                };
                // 通知 UI 切换到"空闲"展示（延后到锁外触发）
                callbacks.Add(() => OnStatusChanged?.Invoke("(空闲)", "用户离开", "空闲"));
            }
            // 空闲分支到此为止，本轮不再采样前台窗口
            return;
        }

        // 用户回来了 — 如果当前是空闲状态，强制结束空闲开始新活动
        // 先把刚结束的"空闲"时段作为一条完整记录收尾落库
        if (_currentActivity != null && _currentActivity.IsIdle)
        {
            FinishCurrentActivity(callbacks);
            // 清空 last 记录，强制下面的切换逻辑触发开始新活动
            _lastProcessName = "";
            _lastWindowTitle = "";
        }

        // 获取当前前台窗口的进程名和标题（通过 Win32 API）
        IntPtr hWnd = Win32Api.GetForegroundWindow();
        // 拿不到前台窗口句柄（锁屏/安全桌面/切换瞬间）则放弃本轮采样
        if (hWnd == IntPtr.Zero) return;

        // 由窗口句柄反查所属进程名与窗口标题
        string processName = Win32Api.GetProcessName(hWnd);
        string windowTitle = Win32Api.GetWindowTitle(hWnd);

        // 窗口标题为空时用进程名兜底（全屏游戏/DirectX 独占可能拿不到标题）
        if (string.IsNullOrEmpty(windowTitle))
            windowTitle = processName;

        // 用分类器给当前活动归类
        string category = _classifier.Classify(processName, windowTitle);

        // 通知 UI 更新实时状态（延后到锁外触发）
        callbacks.Add(() => OnStatusChanged?.Invoke(processName, windowTitle, category));

        // 进程名或标题变了 = 切换了软件 — 结束旧活动，开始新活动
        // 标题也参与比较：同一浏览器切标签页也会被视为切换（便于按网页细分统计）
        if (processName != _lastProcessName || windowTitle != _lastWindowTitle)
        {
            callbacks.Add(() => OnAppSwitched?.Invoke());  // 通知截图服务（锁外触发，避免截图阻塞轮询）
            // 结束旧活动：补结束时间、写库、收集 OnActivityRecorded 回调
            FinishCurrentActivity(callbacks);

            // 开启新活动记录：进程/标题/类别取自本次采样，起点为当前时刻
            _currentActivity = new ActivityRecord
            {
                ProcessName = processName,
                WindowTitle = windowTitle,
                Category = category,
                StartTime = DateTime.Now,
                IsIdle = false
            };

            // 记住本次结果，作为下一轮比较"是否切换"的基准
            _lastProcessName = processName;
            _lastWindowTitle = windowTitle;
        }
        // 否则就是同一个软件继续用，什么都不做，等切换时再算时长
    }

    /// <summary>
    /// 结束当前活动：计算时长，存入数据库，收集回调。只记录超过 1 秒的活动。
    /// </summary>
    /// <param name="callbacks">收集锁外要触发的事件回调</param>
    private void FinishCurrentActivity(List<Action> callbacks)
    {
        // 没有进行中的活动则无事可做
        if (_currentActivity == null) return;

        // 补记结束时间，并把持续时长折算成整秒
        _currentActivity.EndTime = DateTime.Now;
        _currentActivity.Duration = (int)(_currentActivity.EndTime - _currentActivity.StartTime).TotalSeconds;

        // 过滤掉一闪而过的窗口（不足 1 秒的不记录）
        if (_currentActivity.Duration >= 1)
        {
            // 同步写 SQLite；失败只记日志——丢一条记录好过让整个追踪崩溃
            try
            {
                _currentActivity.Id = ActivityRepository.Insert(_currentActivity);
            }
            catch (Exception ex)
            {
                Logger.Error("活动写入数据库失败", ex);
            }

            // 收集回调：延后到锁外触发，避免 DB/UI/截图回调长时间占用 _lock
            // 用局部变量捕获当前记录，防止闭包读到之后被置空的字段
            var recorded = _currentActivity;
            callbacks.Add(() => OnActivityRecorded?.Invoke(recorded));
        }

        // 置空表示当前没有进行中的活动
        _currentActivity = null;
    }
}
