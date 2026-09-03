// ============================================================================
// ScreenshotService.cs — 定时/切换应用截屏服务
// 职责：按设置间隔定时全屏截图（JPEG/PNG、可调质量），或应用切换时立即截屏；
//       写入截图目录并登记 Screenshots 表；受容量/天数上限清理策略约束。
// 线程模型：System.Threading.Timer 后台触发，截图在后台线程执行。
// ============================================================================
// —— 命名空间导入：GDI+ 绘图与图像编码 / IO 与 LINQ / Win32 P/Invoke / 项目内仓储与助手 ——
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using TimeActivity.Services;
using System.Runtime.InteropServices;
using TimeActivity.Data;
using TimeActivity.Helpers;

namespace TimeActivity.Services;

/// <summary>
/// 截图服务 — 定时截屏 + 切换应用时截屏，仿 ManicTime
/// </summary>
public class ScreenshotService
{
    // ==================== 字段与配置 ====================

    // 截图间隔（分钟），默认 5 分钟
    /// <summary>定时截屏间隔（分钟）。来自设置 ScreenshotIntervalMinutes，构造/ReloadSettings 时被夹到 1~1440。</summary>
    private int _intervalMinutes;
    // 截图保存目录
    /// <summary>截图根目录。来自设置 ScreenshotPath，未配置或不可用时为程序目录下 screenshots/。</summary>
    private string _screenshotDir = "";
    // 定时截屏的计时器
    /// <summary>周期截屏定时器（后台线程回调）；Stop 时 Dispose 并置 null。</summary>
    private System.Threading.Timer? _timer;
    // 是否正在运行
    /// <summary>服务是否处于运行态：Start 置 true、Stop 置 false，切换截屏与定时截屏都看它。</summary>
    private bool _running;

    // 是否在切换应用时截屏（仿 ManicTime）
    /// <summary>是否开启"应用一切换就截屏"。来自设置 ScreenshotOnSwitch（默认 true）。</summary>
    private bool _captureOnSwitch;

    // 切换应用截屏的冷却时间：避免极短时间内反复切换（如连续 Alt-Tab）造成截图风暴
    /// <summary>切换截屏的最小间隔：3 秒内只认第一次切换，之后的一律丢弃。</summary>
    private static readonly TimeSpan SwitchCaptureCooldown = TimeSpan.FromSeconds(3);

    // 上次切换截屏的时间（UTC），用于冷却判断
    // 该字段的读改写在 OnAppSwitched 中未加锁，极端并发下可能多截一张，无害
    /// <summary>上一次"切换截屏"发生的时刻（UTC）。初值 DateTime.MinValue 表示从未截过，首次切换必定放行。</summary>
    private DateTime _lastSwitchCaptureUtc = DateTime.MinValue;

    // 串行化锁：定时器回调与 OnAppSwitched 后台线程可能并发进入截图/清理，
    // 加锁避免同秒截图互相覆盖、清理与写入交错导致刚写的就被删或库里留重复记录。
    /// <summary>截屏/清理串行化锁：定时回调与切换回调可能并发进入，靠它避免同名文件互覆与统计错乱。</summary>
    private readonly object _capLock = new();

    // 记录上次清理日期，同一天只清理一次
    /// <summary>上次执行清理的日期（本地日期）；与 DateTime.Today 不同才触发下一轮清理。</summary>
    private DateTime _lastCleanDate = DateTime.MinValue;

    // ==================== Win32 API ====================

    // Win32 API：获取屏幕尺寸（传 nIndex 参数指定要获取宽还是高）
    /// <summary>
    /// 取系统度量值（屏幕尺寸、边框厚度等）。来自 user32.dll。
    /// </summary>
    /// <param name="nIndex">要查询的度量项索引（这里只用 SM_CXSCREEN / SM_CYSCREEN）</param>
    /// <returns>该项的像素值；索引非法时返回 0</returns>
    /// <remarks>
    /// SM_CXSCREEN/SM_CYSCREEN 返回的是"主显示器"分辨率（不是所有显示器拼接的虚拟桌面），
    /// 多显示器环境下用 SM_XVIRTUALSCREEN 系列才能拿到整块虚拟屏，本程序当前只截主屏。
    /// 同时它受 DPI 虚拟化影响：进程未声明高 DPI 感知时拿到的是缩放后的逻辑像素，
    /// 与 CopyFromScreen 拷到的实际像素可能不一致（会截出缺一角的图）。
    /// </remarks>
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>GetSystemMetrics 的索引：主显示器宽度（像素）。</summary>
    private const int SM_CXSCREEN = 0;  // 屏幕宽度索引
    /// <summary>GetSystemMetrics 的索引：主显示器高度（像素）。</summary>
    private const int SM_CYSCREEN = 1;  // 屏幕高度索引

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 构造函数：立即读取一次设置，保证间隔/目录等字段可用。
    /// </summary>
    public ScreenshotService()
    {
        ReloadSettings();
    }

    /// <summary>
    /// 从数据库重新读取截图相关设置（间隔、路径、格式、开关等）。
    /// 路径无效时回退到程序目录下的 screenshots/ 文件夹。
    /// </summary>
    /// <remarks>
    /// 只刷新字段，不重启定时器：若服务已在运行，改了间隔要等下次 Start() 才生效
    /// （定时器在创建时就固定了周期，这里没有做热更新）。
    /// 另外"格式/质量"两项是每次截图时现读的，改了立刻生效。
    /// </remarks>
    public void ReloadSettings()
    {
        // 截图间隔，限制在 1~1440 分钟
        // 解析失败或非法值一律回退默认 5 分钟，防止定时器被配坏
        // 1440 = 24 小时，即允许的最大间隔；Math.Clamp 同时挡住 0、负数与超大值
        _intervalMinutes = int.TryParse(
            SettingsRepository.Get("ScreenshotIntervalMinutes", "5"), out int iv) && iv > 0
            ? Math.Clamp(iv, 1, 1440) : 5;

        // 是否启用"应用切换时立即截屏"
        // 严格比较 "true"：大小写敏感，其它任何值（含 "True"）都视为关闭
        _captureOnSwitch = SettingsRepository.Get("ScreenshotOnSwitch", "true") == "true";

        // 截图保存路径：用户配了就用配的，没配就用程序目录下 screenshots/
        string? userPath = SettingsRepository.Get("ScreenshotPath", "");
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            // 校验路径合法性
            try
            {
                _screenshotDir = userPath;
                // 用"尝试创建目录"来验证路径可写可用
                Directory.CreateDirectory(_screenshotDir);
            }
            catch (Exception ex)
            {
                Logger.Error($"截图路径无效「{userPath}」，回退到默认路径", ex);
                // 回退默认路径：程序目录下的 screenshots/
                _screenshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
                try { Directory.CreateDirectory(_screenshotDir); }
                catch (Exception ex2) { Logger.Error("创建截图目录失败", ex2); }
            }
        }
        else
        {
            // 未配置路径：使用程序目录下 screenshots/
            _screenshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
            // 注意：这一句没有 try 保护（与上面用户路径分支不一致）——
            // 程序装在无写权限目录（如 Program Files）时 CreateDirectory 会抛异常，
            // 并一路冒泡到 Start()/构造函数的调用方
            Directory.CreateDirectory(_screenshotDir);
        }
    }

    // ==================== 启停控制 ====================

    /// <summary>
    /// 启动截图服务。检查开关、加载设置、立即截一张、启动定时器。
    /// </summary>
    public void Start()
    {
        // 幂等保护：已在运行不重复启动
        if (_running) return;
        // 设置里没开截图功能就不启动（EnableScreenshot 默认 "false"，即默认关闭截图）
        if (SettingsRepository.Get("EnableScreenshot", "false") != "true") return;
        // 启动前重读一遍设置，保证间隔/目录用的是最新配置
        ReloadSettings();
        // 置运行标志：此后切换截屏与定时截屏才会真正工作
        _running = true;

        // 启动时清理一次旧截图
        CleanOldScreenshots();
        // 记录"今天已清理"，避免定时器回调当天重复扫目录
        _lastCleanDate = DateTime.Today;

        // 立即截一张
        CaptureAndSave();

        // 启动定时器，每隔 N 分钟截一张（清理单独走天级检查）
        // 回调里顺带做跨天清理检查；首次触发延迟 = 间隔（不会立刻又截一张）
        _timer = new System.Threading.Timer(
            // 周期回调体：先截一张图，再检查是否跨天需要清理（两者内部都自带异常兜底）
            _ => { CaptureAndSave(); MaybeCleanOldScreenshots(); },
            null,
            // 首次触发延迟：一个完整间隔
            // （首个间隔之前已经手动截了一张，这里再等一个间隔才第二次截）
            TimeSpan.FromMinutes(_intervalMinutes),
            // 后续重复间隔：同样为一个间隔
            // 定时器回调跑在线程池线程上；系统休眠/睡眠期间不触发，唤醒后按计划继续
            TimeSpan.FromMinutes(_intervalMinutes));
    }

    /// <summary>
    /// 停止截图服务，释放定时器。
    /// </summary>
    public void Stop()
    {
        // 先摘掉运行标志，OnAppSwitched 立即失效
        _running = false;
        // 释放定时器并置空，终止后续定时截屏
        // 注意：Dispose 不等待"正在进行"的截图——若此刻有回调正在持 _capLock 截图，
        // 它会先跑完再退出，Stop 只是保证不会有下一次触发
        _timer?.Dispose();
        _timer = null;
    }

    // ==================== 清理策略 ====================

    /// <summary>
    /// 每天只清理一次旧截图（跨天时触发），避免每次截屏都扫目录
    /// </summary>
    private void MaybeCleanOldScreenshots()
    {
        // 只有跨天了才执行清理；同一天内的多次触发直接跳过
        // _lastCleanDate 未加锁也非 volatile：定时器线程与切换回调线程可能并发写，
        // 最坏结果是同一天多清一次（清理本身有锁保护），可以接受
        if (_lastCleanDate != DateTime.Today)
        {
            CleanOldScreenshots();
            // 更新清理日期标记
            _lastCleanDate = DateTime.Today;
        }
    }

    /// <summary>
    /// 清理旧截图 — 支持按最大天数和最大总大小两种策略，仿 ManicTime 存储限制。
    /// 优先删最老的文件。
    /// </summary>
    private void CleanOldScreenshots()
    {
        // 全程持锁串行化：避免与并发进行的截图写入交错（边删边写导致误删/漏删统计）
        lock (_capLock)
        {
        // 清理整体兜底：目录被临时占用/权限变化等任何意外都只记日志，绝不能中断截图主流程
        try
        {
            // 目录不存在说明从未截过图，无需清理
            if (!Directory.Exists(_screenshotDir)) return;

            // 分别读取两个清理策略开关：按总大小限制 / 按最大保留天数
            bool enableMaxSize = SettingsRepository.Get("EnableMaxSize", "true") == "true";
            bool enableMaxAge = SettingsRepository.Get("EnableMaxAge", "true") == "true";

            // 递归扫描目录下所有 jpg/png/jpeg 文件，包装成 FileInfo 并按创建时间从老到新排序
            var files = Directory.GetFiles(_screenshotDir, "*.*", SearchOption.AllDirectories)
                // 只保留图片扩展名（忽略大小写比较）
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                // 转 FileInfo，便于读取大小/时间并调用删除
                .Select(f => new FileInfo(f))
                // 创建时间最早（最老）的排在最前，清理时优先删除
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按最大年龄清理：超过天数限制的旧截图直接删
            if (enableMaxAge && int.TryParse(SettingsRepository.Get("MaxScreenshotAgeDays", "30"), out int maxAge))
            {
                // 计算截止时间点：当前时间往前推保留天数
                var cutoff = DateTime.Now.AddDays(-maxAge);
                // 逐个检查：创建时间早于截止点的文件删除
                foreach (var f in files)
                {
                    if (f.CreationTime < cutoff)
                    {
                        // 单个文件删除失败（被占用等）只记日志，继续处理其余文件
                        // 2026-09-02 H4 修复：文件删除成功后同步删除 Screenshots 表对应行，
                        // 消除"文件已删、索引行还在"的幻影行（表行删除失败仅记日志，不阻断）
                        try { f.Delete(); ScreenshotRepository.DeleteByPath(f.FullName); }
                        catch (Exception ex) { Logger.Error($"删除旧截图失败: {f.Name}", ex); }
                    }
                }
                // 从列表中剔除已删除的文件，保证后续大小统计准确
                files = files.Where(f => f.Exists).ToList();
            }

            // 按最大总大小清理：超了就从最老的开始删
            if (enableMaxSize && int.TryParse(SettingsRepository.Get("MaxScreenshotSizeMB", "5120"), out int maxMB))
            {
                // 上限由 MB 换算为字节（先转 long 防止 int 溢出）
                long maxBytes = (long)maxMB * 1024 * 1024;
                // 当前留存文件的总字节数
                long currentSize = files.Sum(f => f.Length);

                // 只要仍超限且还有文件，就持续删除最老的直到达标
                while (currentSize > maxBytes && files.Count > 0)
                {
                    // 取当前最老的一个文件
                    var oldest = files[0];
                    try
                    {
                        // 先从累计大小中扣减，再删除文件；并同步删除表行（2026-09-02 H4 修复）
                        currentSize -= oldest.Length;
                        oldest.Delete();
                        ScreenshotRepository.DeleteByPath(oldest.FullName);
                    }
                    catch (Exception ex) { Logger.Error($"删除旧截图失败: {oldest.Name}", ex); }
                    // 无论删除成功与否都移出列表，保证循环必然收敛
                    files.RemoveAt(0);
                }
            }
        }
        catch (Exception ex)
        {
            // 清理整体兜底：任何意外（目录被占用等）只记日志，绝不影响截图主流程
            Logger.Error("截图清理失败", ex);
        }
        }
    }

    // ==================== 切换截屏 ====================

    /// <summary>
    /// 切换应用时调用 — 仿 ManicTime "在每次应用程序切换时截屏"。
    /// 受冷却时间限制，避免快速连续切换时疯狂截屏。
    /// 注意：不再重置周期定时器，否则每次切换都会把"每 N 分钟定时截屏"的计时归零，
    /// 导致定时截屏长期不触发（与设置里的截图间隔互相矛盾）。
    /// </summary>
    public void OnAppSwitched()
    {
        // 未运行或未开启切换截屏功能则直接忽略
        if (!_running || !_captureOnSwitch) return;

        // 冷却：距上次切换截屏不足冷却时间就跳过，挡住截图风暴
        // 使用 UTC 时间戳做冷却计时，不受时区影响
        var now = DateTime.UtcNow;
        if (now - _lastSwitchCaptureUtc < SwitchCaptureCooldown)
            return;
        // 通过冷却检查，记录本次时刻并开启新的冷却窗口
        _lastSwitchCaptureUtc = now;

        // 立即截屏保存（内部有锁串行化，与定时截屏互斥）
        CaptureAndSave();
        // 顺带做一次跨天清理检查
        MaybeCleanOldScreenshots();
    }

    // ==================== 截屏与保存 ====================

    /// <summary>
    /// 截屏并保存到磁盘。用 GetSystemMetrics 拿屏幕尺寸，GDI 截屏，
    /// 支持 JPG（可调质量）和 PNG 两种格式，保存后写一条数据库记录。
    /// </summary>
    private void CaptureAndSave()
    {
        // 全程持锁：定时截屏与切换截屏可能同时进入，串行化避免同名文件互覆与脏统计
        lock (_capLock)
        {
        // 整体兜底：截屏/编码/落库任何环节出错都只记日志，让后台线程静默存活继续下一轮
        try
        {
            // 用 Win32 API 获取屏幕分辨率
            // 注意：仅主屏幕尺寸，多显示器环境只截主屏
            int width = GetSystemMetrics(SM_CXSCREEN);
            int height = GetSystemMetrics(SM_CYSCREEN);

            // GDI 截屏：创建 Bitmap + CopyFromScreen
            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                // 从屏幕左上角 (0,0) 开始把整个主屏像素拷贝到位图
                g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
            }

            // 根据设置选择保存格式
            string format = SettingsRepository.Get("ScreenshotFormat", "jpg") ?? "jpg";
            // 非 png 一律按 jpg 处理（扩展名同步统一）
            string ext = format == "png" ? "png" : "jpg";
            // 文件名含时分秒+毫秒（2026-09-02 H3 修复：原秒级精度在定时截图与切换截图
            // 同秒触发时会互相覆盖丢图；截图全程持 _capLock 串行，毫秒精度已足够防冲突）
            string fileName = $"screenshot_{DateTime.Now:HH-mm-ss-fff}.{ext}";
            // 按日期分文件夹：screenshots/2026-08-15/screenshot_14-30-00.jpg
            string dateDir = Path.Combine(_screenshotDir, DateTime.Now.ToDateKey());
            // 确保当日子目录存在（多线程下 CreateDirectory 对已存在目录是幂等的）
            Directory.CreateDirectory(dateDir);
            // 完整落盘路径 = 日期子目录 + 毫秒级文件名
            string filePath = Path.Combine(dateDir, fileName);

            if (format == "png")
            {
                // PNG 无损直存
                bmp.Save(filePath, ImageFormat.Png);
            }
            else
            {
                // JPG 模式：根据设置调整压缩质量（high=80, medium=50, low=30）
                // 2026-08-25：补 using —— EncoderParameters 含非托管句柄，原实现每次 JPG 截图泄漏少量资源
                using var encoderParams = new EncoderParameters(1);
                // 把质量档位映射为 0~100 的长整型编码参数
                var quality = SettingsRepository.Get("ScreenshotQuality", "medium") switch
                {
                    "high" => 80L,
                    "low" => 30L,
                    // 默认（含 medium 与未知值）取中等质量
                    _ => 50L
                };
                // 将质量参数装入编码参数数组的第 0 项
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                // 查找系统 JPEG 编码器
                var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                // 用指定编码器与质量参数落盘
                bmp.Save(filePath, jpegEncoder, encoderParams);
            }

            // 存数据库：如果是程序目录下的路径就用相对路径，否则用绝对路径
            // 读取刚落盘文件的字节大小用于入库
            var fileSize = new FileInfo(filePath).Length;
            // exe 所在目录作为"程序目录内/外"的判定基准
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            // 程序目录内的路径去掉前缀存相对路径，便于程序挪动目录后记录仍有效
            string dbPath = filePath.StartsWith(appDir) ? filePath.Substring(appDir.Length) : filePath;
            // 登记 Screenshots 表（相对/绝对路径 + 文件大小）
            ScreenshotRepository.Insert(dbPath, fileSize);
        }
        // 目录无写权限：给出明确指引（提示用户去设置改路径）
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error($"截图保存失败：路径「{_screenshotDir}」无写入权限，请在设置中修改截图保存路径", ex);
        }
        catch (Exception ex)
        {
            // 其余一切异常统一兜底记日志，后台线程静默存活
            Logger.Error("截图保存失败", ex);
        }
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 查找指定图片格式对应的编码器（JPG/PNG 等）。
    /// </summary>
    /// <param name="format">目标图片格式</param>
    /// <returns>对应的编码器信息</returns>
    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        // 枚举系统安装的全部图像编码器
        var codecs = ImageCodecInfo.GetImageEncoders();
        foreach (var codec in codecs)
        {
            // 格式 GUID 相同即为对应编码器
            if (codec.FormatID == format.Guid)
                return codec;
        }
        // 兜底返回第一个编码器（正常情况不会走到）
        return codecs[0];
    }

    /// <summary>
    /// 获取屏幕宽度（通过 Win32 GetSystemMetrics）。
    /// </summary>
    public static int GetScreenWidth() => GetSystemMetrics(SM_CXSCREEN);

    /// <summary>
    /// 获取屏幕高度（通过 Win32 GetSystemMetrics）。
    /// </summary>
    public static int GetScreenHeight() => GetSystemMetrics(SM_CYSCREEN);

    /// <summary>
    /// 获取某个时间段内的截图（活动开始~结束之间拍的）
    /// </summary>
    public static string? GetScreenshotForTime(DateTime startTime, DateTime endTime)
    {
        // 直接委托仓储层按时间范围查询截图记录
        return ScreenshotRepository.GetForTimeRange(startTime, endTime);
    }
}
