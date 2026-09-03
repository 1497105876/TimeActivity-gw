// ============================================================================
// Win32Api.cs — Win32 API 的 P/Invoke 封装（静态类）
// 职责：取前台窗口句柄、由句柄取进程名/窗口标题、取最后一次输入的空闲秒数。
// 全部为无状态静态调用，供 TrackingEngine 每轮采样使用。
// ============================================================================
// 基础类型（IntPtr 等）
using System;
// DllImport / StructLayout / Marshal 等互操作设施
using System.Runtime.InteropServices;

namespace TimeActivity.Services;

/// <summary>
/// Win32 API 封装 — 用来抓当前前台窗口的信息（进程名、窗口标题）与用户空闲时长。
/// </summary>
/// <remarks>
/// 调用方：TrackingEngine 每轮采样（默认 3 秒一次，后台线程）会依次调用
/// GetIdleSeconds / GetForegroundWindow / GetProcessName / GetWindowTitle。
/// 因此这几个方法必须是"无状态、线程安全、不抛托管异常"的，否则会打断采样循环。
/// 平台：全部为 user32/kernel32 的用户态 API，仅 Windows 可用（本项目就是 WPF/Windows 桌面应用）。
/// </remarks>
public static class Win32Api
{
    // ======================================================================
    // P/Invoke 声明区
    // 注意：Win32 函数失败时不抛托管异常，仅返回 0/false，需自行判错；
    //       SetLastError 未开启，故此处不做 Marshal.GetLastWin32Error 取错。
    // ======================================================================
    // Win32 API：获取当前前台窗口句柄（用户正在操作的窗口）
    // 返回：前台窗口句柄；可能为 IntPtr.Zero（锁屏/UAC 安全桌面/切换瞬间无前台窗口）
    /// <summary>
    /// 获取当前前台窗口（用户正在操作的窗口）的句柄。来自 user32.dll。
    /// </summary>
    /// <returns>
    /// 前台窗口的 HWND；可能为 <see cref="IntPtr.Zero"/>（桌面无焦点、锁屏、UAC 安全桌面、
    /// 或窗口正在切换的瞬间），调用方必须判零后再使用。
    /// </returns>
    /// <remarks>
    /// 仅对"调用线程所在桌面"有效：服务会话/其他用户会话里拿不到，本程序作为用户态桌面应用不受影响。
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // 获取窗口标题文字
    // CharSet.Unicode → 绑定宽字符版 GetWindowTextW；text 为接收缓冲区，
    // count 为可容纳的最大字符数（含结尾 \0）；返回实际复制字符数，失败返回 0
    /// <summary>
    /// 取指定窗口的标题栏文字。来自 user32.dll，绑定宽字符版本 GetWindowTextW。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄</param>
    /// <param name="text">接收缓冲区（StringBuilder），由调用方预分配容量</param>
    /// <param name="count">缓冲区可容纳的最大字符数（含结尾 '\0'）</param>
    /// <returns>实际复制的字符数；窗口无标题或句柄失效时返回 0（此时缓冲区保持原样）</returns>
    /// <remarks>
    /// 只能取"同一进程/普通权限可见"窗口的文字；部分 UWP、以管理员权限运行的窗口可能取到空串。
    /// 注意它不返回托管异常，失败只体现在返回值为 0。
    /// </remarks>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    // 通过窗口句柄获取对应的进程 ID
    // 返回值是创建该窗口的线程 ID（本处不使用）；processId 以 out 参数带回进程 PID，
    // 句柄无效时 processId = 0
    /// <summary>
    /// 由窗口句柄反查"创建该窗口的线程 ID"并带出所属进程 PID。来自 user32.dll。
    /// </summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <param name="processId">输出参数：窗口所属进程 PID；句柄无效时为 0</param>
    /// <returns>创建该窗口的线程 ID（本程序不使用该返回值）</returns>
    /// <remarks>
    /// 这里不校验返回值：后续用 PID 反查进程时会 try/catch 兜底（见 <see cref="GetProcessName"/>），
    /// pid=0 时 Process.GetProcessById 必然抛异常，走 "unknown" 分支。
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Win32 结构体：记录最后一次输入操作的时间
    // LayoutKind.Sequential：按声明顺序平铺布局，字段顺序/类型必须与 Win32 定义严格一致
    /// <summary>
    /// Win32 LASTINPUTINFO 结构体：承载"系统最后一次键鼠输入"的时间戳。
    /// </summary>
    /// <remarks>
    /// 字段顺序与类型必须与 Win32 头文件定义严格一致，否则 P/Invoke 时内存布局错位
    /// （读到的 dwTime 是垃圾值 → 空闲判定完全失真）。因此禁止调整字段顺序或增删字段。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        /// <summary>结构体自身字节大小。调用 GetLastInputInfo 前必须由调用方填写，Win32 用它判定版本。</summary>
        public uint cbSize;  // 结构体大小
        /// <summary>最后一次输入事件发生时的 GetTickCount 计数值（毫秒，会随 49.7 天回绕）。</summary>
        public uint dwTime;  // 最后一次输入的时间戳（GetTickCount 值）
    }

    // 获取系统最后一次输入信息（用来计算用户空闲了多久）
    // 约定：调用方必须先填 plii.cbSize；返回 false 表示调用失败，此时 dwTime 内容不可信
    /// <summary>
    /// 取系统最后一次键鼠输入的时间信息。来自 user32.dll。
    /// </summary>
    /// <param name="plii">
    /// 传入/传出结构体。调用前必须把 cbSize 填成结构体大小（Win32 用它识别结构版本），
    /// 成功时 dwTime 被填上"最后一次输入"的 GetTickCount 值。
    /// </param>
    /// <returns>成功返回 true；返回 false 表示调用失败，此时 dwTime 内容不可信（保持调用前的值）</returns>
    /// <remarks>
    /// 该时间是会话级的：只统计当前交互会话的输入，别的会话（远程桌面）不算。
    /// </remarks>
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // 获取系统启动以来的毫秒数（64 位不会溢出）
    /// <summary>
    /// 取系统启动以来经过的毫秒数（64 位，约 5.8 亿年才回绕，实际不会溢出）。来自 kernel32.dll。
    /// </summary>
    /// <returns>系统启动后的毫秒数</returns>
    /// <remarks>
    /// 本类当前未使用它（空闲计算走 <see cref="GetTickCount"/> 以便与 dwTime 同为 uint 直接相减），
    /// 保留它是给需要"绝对不回绕"场景的调用方使用。
    /// </remarks>
    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    // 获取系统启动以来的毫秒数（32 位，约49.7天回绕，但 uint 减法自动处理回绕）
    /// <summary>
    /// 取系统启动以来经过的毫秒数（32 位，约 49.7 天回绕一次）。来自 kernel32.dll。
    /// </summary>
    /// <returns>系统启动后的毫秒数（低 32 位）</returns>
    /// <remarks>
    /// 与 LASTINPUTINFO.dwTime 同为 uint，两者直接相减可利用无符号溢出自动处理回绕，
    /// 因此这里刻意不用 GetTickCount64（那需要先做一次 &amp; 0xFFFFFFFF 转换）。
    /// </remarks>
    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    /// <summary>
    /// 获取当前前台窗口的标题文字。
    /// </summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <returns>窗口标题字符串</returns>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        // 预分配 512 字符缓冲区；超长标题会被 Win32 截断而非报错
        // 容量 512 与下方 GetWindowText 的 count 参数一致：count 含结尾 '\0'，即最多 511 个有效字符
        var sb = new System.Text.StringBuilder(512);
        // 句柄失效时返回 0 且 sb 保持空串，ToString() 自然得到 ""
        // 返回值（实际复制字符数）这里刻意不判：0 与非 0 对本方法的结果没有区别
        GetWindowText(hWnd, sb, 512);
        // 无标题窗口/受保护进程会拿到空串，由调用方自行兜底（TrackingEngine 用进程名兜底）
        return sb.ToString();
    }

    /// <summary>
    /// 获取当前前台窗口对应的进程名（不含 .exe 后缀）。
    /// </summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <returns>进程名，获取失败返回 "unknown"</returns>
    public static string GetProcessName(IntPtr hWnd)
    {
        // 通过窗口句柄拿进程 ID；句柄失效时 pid 为 0
        GetWindowThreadProcessId(hWnd, out uint pid);
        try
        {
            // Process 对象用完必须释放，否则长时间运行会耗尽 OS 句柄
            // (int)pid 强转安全：Windows PID 上限远小于 int.MaxValue
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            // ProcessName 不含 .exe 后缀（如 chrome、devenv），正好是统计与分类想要的粒度
            return proc.ProcessName;
        }
        catch
        {
            // 典型场景：pid=0（无效句柄）或目标进程已退出 → GetProcessById 抛异常
            // 还有一类是"两步之间进程刚好退出"的竞态，无法彻底避免，只能兜底
            return "unknown";
        }
    }

    /// <summary>
    /// 获取用户空闲了多久（秒）。原理：GetTickCount - 最后一次输入时间。
    /// uint 减法自动处理 49.7 天回绕（uint 溢出后减法结果仍然正确）。
    /// </summary>
    /// <returns>空闲秒数</returns>
    public static int GetIdleSeconds()
    {
        // 实例化结构体并填 cbSize —— Win32 约定：调用前必须先写结构体大小，
        // 否则 GetLastInputInfo 可能直接失败或写坏内存
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        // 调用失败（如会话锁定/权限异常）时 info.dwTime 不会被填充，dwTime 保持 0，
        // 会让 elapsed 变成"开机以来的全部时间"，误判为永久空闲、追踪静默失效。
        // 失败按空闲 0 秒处理并记录一条 WARN，便于排查。
        if (!GetLastInputInfo(ref info))
        {
            Logger.Warning("获取最后输入信息失败（GetLastInputInfo 返回 false），按空闲 0 秒处理");
            return 0;
        }
        // 用 GetTickCount（uint）与 dwTime（uint）做差，uint 减法自动处理回绕
        // 例：dwTime=0xFFFFFFFF（回绕前最后一刻），now=0x00000001（回绕后）
        // uint 减法：0x00000001 - 0xFFFFFFFF = 0x00000002（即 2ms，正确）
        uint now = GetTickCount();
        uint elapsed = now - info.dwTime;
        // ms → 秒；elapsed 最大约 49.7 天（≈4294967 秒），转 int 不会溢出
        // 结果恒为非负数：uint 相减不会出现负值，最坏情况（dwTime=0）等于系统已运行秒数
        return (int)(elapsed / 1000);
    }
}
