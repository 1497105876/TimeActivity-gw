// ============================================================================
// TrayIcon.cs — 系统托盘图标封装（纯 Win32 Shell_NotifyIcon 实现）
// 职责：创建/更新/销毁托盘图标；解析鼠标消息为 双击/左键/右键 语义事件；
//       动态构建上下文菜单（开始/停止、显示主界面、退出）。
// 消息回传依赖宿主窗口句柄（MainWindow 通过 WndProc 转发 WM_TRAYICON）。
// ============================================================================
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;

namespace TimeActivity.Services;

/// <summary>
/// 系统托盘图标（纯 Win32 API，不引 WinForms）
/// </summary>
public class TrayIcon : IDisposable
{
    // Win32 API：操作系统托盘图标（添加/修改/删除）
    // dwMessage 指定操作类型（NIM_ADD/NIM_MODIFY/NIM_DELETE），pnid 是托盘图标数据
    // 返回 false 表示失败（如 NIM_ADD 时托盘尚未就绪/参数非法），本类未检查该返回值
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW pnid);

    // 加载系统预定义图标（如 IDI_APPLICATION）
    // hInstance=NULL + lpIconName=资源编号 → 加载系统共享图标；共享图标不可 DestroyIcon
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    // 创建弹出菜单（右键菜单）
    // 返回菜单句柄，用完必须 DestroyMenu，否则内核对象泄漏
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    // 往菜单追加菜单项（文字项或分隔符）
    // uFlags 决定 uIDNewItem/lpNewItem 的解释方式（MF_STRING/MF_SEPARATOR）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    // 在指定位置显示弹出菜单，返回用户选择的菜单项 ID
    // 同步阻塞直至用户选择/取消；TPM_RETURNCMD 使返回值为所选项 ID（取消为 0），
    // 否则返回值是布尔。经典要求：调用前需 SetForegroundWindow(宿主)，否则点击别处菜单不消失
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    // 销毁菜单，用完必须调
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    // 从文件或资源加载图片（图标/光标/位图），这里用来加载系统图标资源
    // uType: 1=IMAGE_ICON；fuLoad 为 LR_* 标志组合
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    // 托盘操作消息类型
    private const int NIM_ADD = 0x00000000;    // 添加图标到托盘
    private const int NIM_MODIFY = 0x00000001; // 修改已有托盘图标
    private const int NIM_DELETE = 0x00000002; // 从托盘删除图标

    // 托盘数据标志位：通知消息+图标+提示文字
    // uFlags 声明 NOTIFYICONDATAW 中哪些字段有效
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    // 菜单项类型标志
    private const uint MF_STRING = 0x00000000;     // 文字菜单项
    private const uint MF_SEPARATOR = 0x00000800;  // 分隔线

    // 弹出菜单的显示标志
    private const uint TPM_RIGHTBUTTON = 0x0002;   // 右键也能选择菜单项
    private const uint TPM_LEFTALIGN = 0x0000;     // 菜单左对齐
    private const uint TPM_BOTTOMALIGN = 0x0020;   // 菜单底部对齐（向上弹出）
    private const uint TPM_RETURNCMD = 0x0100;     // 让 TrackPopupMenu 返回选中的菜单项 ID

    // 自定义窗口消息基址，托盘回调消息用 WM_APP+1
    // 用 WM_APP 区间避免与系统消息/框架内部消息冲突
    private const int WM_APP = 0x8000;
    public const int WM_TRAYICON = WM_APP + 1;     // 托盘图标回调消息
    private const int WM_LBUTTONDBLCLK = 0x0203;   // 左键双击
    private const int WM_RBUTTONUP = 0x0205;        // 右键抬起

    // 托盘图标数据结构（Win32 NOTIFYICONDATAW）
    // 此处仅声明到 szTip 的"旧版"布局：未用的 VISTA+ 扩展字段省略可减小封送体积，
    // 但 cbSize 必须与声明一致（Marshal.SizeOf 自动匹配）
    /// <summary>
    /// Win32 NOTIFYICONDATAW 结构的"旧版前缀"子集：仅覆盖本类用到的 cbSize~szTip 字段。
    /// 字段顺序必须与系统头文件一致，禁止调整/删减（P/Invoke 按声明顺序平铺内存）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        /// <summary>结构体字节大小，Win32 据此识别版本；每次调用前由 Marshal.SizeOf 填充。</summary>
        public int cbSize;       // 结构体大小
        /// <summary>接收回调消息的窗口句柄（NIM_ADD 时指定）。</summary>
        public IntPtr hWnd;      // 接收回调消息的窗口句柄
        /// <summary>托盘图标 ID（同一窗口可挂多个，靠 ID 区分）。</summary>
        public uint uID;         // 托盘图标 ID（同一窗口可以有多个托盘图标）
        /// <summary>有效字段掩码：声明 NIF_MESSAGE|NIF_ICON|NIF_TIP 中哪些字段被填充。</summary>
        public uint uFlags;      // 指定哪些字段有效（NIF_MESSAGE|NIF_ICON|NIF_TIP）
        /// <summary>托盘鼠标消息回调号（交互时发给 hWnd 的消息 ID）。</summary>
        public uint uCallbackMessage; // 回调消息号（托盘交互时发给 hWnd）
        /// <summary>要显示的图标句柄。</summary>
        public IntPtr hIcon;     // 图标句柄
        /// <summary>悬停提示文字（宽字符、固定 128 字符上限，含结尾 '\0'）。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;     // 鼠标悬停提示文字
    }

    // 宿主窗口句柄
    /// <summary>接收托盘回调消息的宿主窗口句柄（由 TrayHost 传入，生命周期比本对象长）。</summary>
    private IntPtr _hWnd;
    // 图标句柄
    /// <summary>当前托盘使用的图标句柄（共享系统图标或自有句柄）；Dispose 时按来源决定是否 DestroyIcon。</summary>
    private IntPtr _hIcon;
    // 图标是否已添加到托盘
    /// <summary>图标是否已通过 NIM_ADD 注册到托盘；后续 NIM_MODIFY/NIM_DELETE 都以它为前置条件。</summary>
    private bool _added;
    // 托盘图标 ID
    /// <summary>托盘图标 ID（固定 1）：同一窗口挂多个托盘图标时用于区分，HandleMessage 里按它校验来源。</summary>
    private readonly uint _uID = 1;

    // 用户交互回调（双击托盘图标、右键菜单触发时调用）
    // 公有字段式回调：由宿主（TrayHost）在 InitTray 中赋值
    /// <summary>左键双击托盘图标时触发（宿主一般映射为"显示主窗口"）。</summary>
    public Action? OnDoubleClick;
    /// <summary>右键抬起时触发（宿主一般在此弹右键菜单）。</summary>
    public Action? OnShowMenu;

    // Win32 API：获取鼠标当前位置
    // 屏幕物理坐标；弹出菜单需要用它定位到光标处
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // 释放图标句柄，防止 GDI 泄漏
    // 注意：对 LoadIcon 得到的"共享图标"调用是多余但无害的（返回 false）
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Win32 POINT 结构体（屏幕坐标 x/y）
    /// <summary>Win32 POINT（屏幕坐标），GetCursorPos 的输出目标；两个 int 字段按声明平铺。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>
    /// 构造函数：创建托盘图标，加载默认图标，注册到系统托盘。
    /// </summary>
    /// <param name="hWnd">接收托盘消息的窗口句柄</param>
    /// <param name="tooltip">鼠标悬停时显示的提示文字</param>
    public TrayIcon(IntPtr hWnd, string tooltip = "TimeActivity")
    {
        // 记录宿主句柄：后续 NIM_MODIFY/DELETE 都要用同一句柄+ID 标识
        _hWnd = hWnd;
        // 先拿到图标句柄再注册（NIM_ADD 需要 hIcon）
        _hIcon = LoadDefaultIcon();

        // 构建托盘数据并添加到系统托盘
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(), // 结构体实际字节数，Win32 用版本字段校验
            hWnd = hWnd,
            uID = _uID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,  // 回调消息+图标+提示都有效
            uCallbackMessage = WM_TRAYICON,             // 鼠标交互时向 hWnd 发的回调消息号
            hIcon = _hIcon,
            szTip = tooltip
        };
        // 注册进系统托盘；返回 false 表示失败（如托盘进程尚未就绪），本对象不检查该返回值
        Shell_NotifyIconW(NIM_ADD, ref data);
        // 置标记：此后 UpdateTooltip 允许发 MODIFY、Dispose 需要发 DELETE
        _added = true;
    }

    /// <summary>
    /// 处理托盘消息，返回 true 表示已处理
    /// </summary>
    /// <param name="wParam">WPF WndProc 原样转发的 wParam（低 32 位为图标 ID）</param>
    /// <param name="lParam">鼠标消息码（低 16 位）</param>
    /// <returns>是否为本类的托盘消息并已分发</returns>
    public bool HandleMessage(IntPtr wParam, IntPtr lParam)
    {
        // wParam 校验图标 ID：一个窗口挂多个托盘图标时可区分来源
        if ((int)wParam != _uID) return false;

        // lParam 低 16 位才是实际鼠标消息（高位含坐标等附加信息）
        int msg = (int)lParam & 0xFFFF;
        switch (msg)
        {
            // 左键双击托盘图标：唤醒/显示主窗口
            case WM_LBUTTONDBLCLK:
                OnDoubleClick?.Invoke();
                return true; // 已消费：不再交回 WPF 默认流程
            // 右键抬起：在光标处弹上下文菜单
            case WM_RBUTTONUP:
                OnShowMenu?.Invoke();
                return true; // 已消费：避免触发系统自带菜单
        }
        // 其余鼠标消息（单击移动等）不消费，交回默认处理
        return false;
    }

    /// <summary>
    /// 在鼠标当前位置显示右键菜单
    /// </summary>
    /// <param name="isRunning">当前是否正在追踪（决定菜单文案）</param>
    public void ShowContextMenuAtCursor(bool isRunning)
    {
        // 先取光标屏幕坐标，再委托给指定坐标版本
        GetCursorPos(out POINT pt);
        ShowContextMenu(pt.X, pt.Y, isRunning);
    }

    /// <summary>
    /// 显示右键菜单（显示主窗口 / 开始或停止追踪 / 退出），并把用户选择分发到对应回调。
    /// 菜单项 ID 约定：1=显示主窗口、2=开始/停止追踪、3=退出。
    /// </summary>
    /// <param name="x">菜单左上角 X 坐标</param>
    /// <param name="y">菜单左上角 Y 坐标</param>
    /// <param name="isRunning">当前是否正在追踪，决定菜单文字显示"开始"还是"停止"</param>
    public void ShowContextMenu(int x, int y, bool isRunning)
    {
        // 创建弹出菜单
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        // 追加菜单项：显示主窗口、开始/停止追踪、分隔线、退出
        // 项 ID 与下方 switch 严格对应：1/2/3 分别映射到三个回调
        AppendMenuW(hMenu, MF_STRING, 1, "显示主窗口");
        AppendMenuW(hMenu, MF_STRING, 2, isRunning ? "停止追踪" : "开始追踪"); // 文案随运行状态切换
        AppendMenuW(hMenu, MF_SEPARATOR, 0, ""); // 分隔线：无 ID 无文案，仅做视觉分组
        AppendMenuW(hMenu, MF_STRING, 3, "退出");

        // 显示菜单并等待用户选择，返回值是菜单项 ID
        int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD, x, y, 0, _hWnd, IntPtr.Zero);
        DestroyMenu(hMenu);  // 用完销毁

        // 根据用户选择触发对应回调
        switch (cmd)
        {
            case 1: OnDoubleClick?.Invoke(); break;   // 显示主窗口
            case 2: OnToggleTracking?.Invoke(); break; // 开始/停止追踪
            case 3: OnExit?.Invoke(); break;           // 退出
        }
    }

    // 切换追踪状态的回调
    /// <summary>菜单项"开始/停止追踪"被选中时触发（由宿主执行真正的启停逻辑）。</summary>
    public Action? OnToggleTracking;
    // 退出程序的回调
    /// <summary>菜单项"退出"被选中时触发（由宿主关主窗并 Shutdown 应用）。</summary>
    public Action? OnExit;

    /// <summary>
    /// 更新托盘图标的鼠标悬停提示文字。
    /// </summary>
    /// <param name="text">新的提示文字</param>
    public void UpdateTooltip(string text)
    {
        // 未成功添加过就不发 MODIFY（避免对不存在的图标做无效操作）
        if (!_added) return;
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = _uID,
            // 只改提示文字，故只带 NIF_TIP；其余字段无需填充
            uFlags = NIF_TIP,
            szTip = text
        };
        // NIM_MODIFY 原地更新提示文字；失败静默（托盘重启等罕见场景下下次会重建）
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// 加载系统默认应用图标（先试 LoadImageW，失败了用 LoadIconW 兜底）。
    /// </summary>
    /// <returns>图标句柄</returns>
    private static IntPtr LoadDefaultIcon()
    {
        // "#32512" 是 IDI_APPLICATION 的资源编号，系统默认应用图标
        // hInst=null + "#编号" 字符串 → 指向系统资源；宽高传 0 使用资源原始尺寸；
        // fuLoad=0x00000010 | 0x00008000（0x8010）：0x8000 是 LR_SHARED（返回系统共享句柄，
        // 共享句柄不可 DestroyIcon），0x10 位按现有实现透传，不在此展开语义
        IntPtr hIcon = LoadImageW(IntPtr.Zero, "#32512", 1, 0, 0, 0x00000010 | 0x00008000);
        // 主路径返回空（例如某些系统版本对 flag 组合处理不同）→ 用 LoadIconW 直接取 IDI_APPLICATION 兜底
        if (hIcon == IntPtr.Zero)
            hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION 兜底
        return hIcon;
    }

    /// <summary>
    /// 释放资源：从系统托盘移除图标。
    /// </summary>
    public void Dispose()
    {
        if (_added)
        {
            // 从托盘删除图标
            // DELETE 只需要 cbSize/hWnd/uID 三个字段
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = _uID
            };
            // 通知系统移除托盘图标；失败静默（应用本身即将退出，无碍）
            Shell_NotifyIconW(NIM_DELETE, ref data);
            // 无论删除是否成功都复位标记，保证 Dispose 幂等、可安全重复调用
            _added = false;
        }

        // 释放图标句柄
        if (_hIcon != IntPtr.Zero)
        {
            // 对"共享图标"调用 DestroyIcon 是多余但无害的（返回 false）；
            // 对自有句柄则必须释放，否则 GDI 句柄泄漏
            DestroyIcon(_hIcon);
            // 置零防止二次释放
            _hIcon = IntPtr.Zero;
        }
    }
}
