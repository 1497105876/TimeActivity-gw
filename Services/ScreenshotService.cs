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
    // 截图间隔（分钟），默认 5 分钟
    private int _intervalMinutes;
    // 截图保存目录
    private string _screenshotDir = "";
    // 定时截屏的计时器
    private System.Threading.Timer? _timer;
    // 是否正在运行
    private bool _running;

    // 是否在切换应用时截屏（仿 ManicTime）
    private bool _captureOnSwitch;

    // 记录上次清理日期，同一天只清理一次
    private DateTime _lastCleanDate = DateTime.MinValue;

    // Win32 API：获取屏幕尺寸（传 nIndex 参数指定要获取宽还是高）
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;  // 屏幕宽度索引
    private const int SM_CYSCREEN = 1;  // 屏幕高度索引

    public bool IsRunning => _running;

    public ScreenshotService()
    {
        ReloadSettings();
    }

    /// <summary>
    /// 从数据库重新读取截图相关设置（间隔、路径、格式、开关等）。
    /// 路径无效时回退到程序目录下的 screenshots/ 文件夹。
    /// </summary>
    public void ReloadSettings()
    {
        // 截图间隔，限制在 1~1440 分钟
        _intervalMinutes = int.TryParse(
            SettingsRepository.Get("ScreenshotIntervalMinutes", "5"), out int iv) && iv > 0
            ? Math.Clamp(iv, 1, 1440) : 5;

        _captureOnSwitch = SettingsRepository.Get("ScreenshotOnSwitch", "true") == "true";

        // 截图保存路径：用户配了就用配的，没配就用程序目录下 screenshots/
        string? userPath = SettingsRepository.Get("ScreenshotPath", "");
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            // 校验路径合法性
            try
            {
                _screenshotDir = userPath;
                Directory.CreateDirectory(_screenshotDir);
            }
            catch (Exception ex)
            {
                Logger.Error($"截图路径无效「{userPath}」，回退到默认路径", ex);
                _screenshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
                try { Directory.CreateDirectory(_screenshotDir); }
                catch (Exception ex2) { Logger.Error("创建截图目录失败", ex2); }
            }
        }
        else
        {
            _screenshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
            Directory.CreateDirectory(_screenshotDir);
        }
    }

    /// <summary>
    /// 启动截图服务。检查开关、加载设置、立即截一张、启动定时器。
    /// </summary>
    public void Start()
    {
        if (_running) return;
        // 设置里没开截图功能就不启动
        if (SettingsRepository.Get("EnableScreenshot", "false") != "true") return;
        ReloadSettings();
        _running = true;

        // 启动时清理一次旧截图
        CleanOldScreenshots();
        _lastCleanDate = DateTime.Today;

        // 立即截一张
        CaptureAndSave();

        // 启动定时器，每隔 N 分钟截一张（清理单独走天级检查）
        _timer = new System.Threading.Timer(
            _ => { CaptureAndSave(); MaybeCleanOldScreenshots(); },
            null,
            TimeSpan.FromMinutes(_intervalMinutes),
            TimeSpan.FromMinutes(_intervalMinutes));
    }

    /// <summary>
    /// 停止截图服务，释放定时器。
    /// </summary>
    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 每天只清理一次旧截图（跨天时触发），避免每次截屏都扫目录
    /// </summary>
    private void MaybeCleanOldScreenshots()
    {
        if (_lastCleanDate != DateTime.Today)
        {
            CleanOldScreenshots();
            _lastCleanDate = DateTime.Today;
        }
    }

    /// <summary>
    /// 清理旧截图 — 支持按最大天数和最大总大小两种策略，仿 ManicTime 存储限制。
    /// 优先删最老的文件。
    /// </summary>
    private void CleanOldScreenshots()
    {
        try
        {
            if (!Directory.Exists(_screenshotDir)) return;

            bool enableMaxSize = SettingsRepository.Get("EnableMaxSize", "true") == "true";
            bool enableMaxAge = SettingsRepository.Get("EnableMaxAge", "true") == "true";

            var files = Directory.GetFiles(_screenshotDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按最大年龄清理：超过天数限制的旧截图直接删
            if (enableMaxAge && int.TryParse(SettingsRepository.Get("MaxScreenshotAgeDays", "30"), out int maxAge))
            {
                var cutoff = DateTime.Now.AddDays(-maxAge);
                foreach (var f in files)
                {
                    if (f.CreationTime < cutoff)
                    {
                        try { f.Delete(); }
                        catch (Exception ex) { Logger.Error($"删除旧截图失败: {f.Name}", ex); }
                    }
                }
                files = files.Where(f => f.Exists).ToList();
            }

            // 按最大总大小清理：超了就从最老的开始删
            if (enableMaxSize && int.TryParse(SettingsRepository.Get("MaxScreenshotSizeMB", "5120"), out int maxMB))
            {
                long maxBytes = (long)maxMB * 1024 * 1024;
                long currentSize = files.Sum(f => f.Length);

                while (currentSize > maxBytes && files.Count > 0)
                {
                    var oldest = files[0];
                    try
                    {
                        currentSize -= oldest.Length;
                        oldest.Delete();
                    }
                    catch (Exception ex) { Logger.Error($"删除旧截图失败: {oldest.Name}", ex); }
                    files.RemoveAt(0);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("截图清理失败", ex);
        }
    }

    /// <summary>
    /// 切换应用时调用 — 仿 ManicTime "在每次应用程序切换时截屏"。
    /// 截完图后重置定时器倒计时，从此刻起重新计时。
    /// </summary>
    public void OnAppSwitched()
    {
        if (!_running || !_captureOnSwitch) return;
        CaptureAndSave();
        MaybeCleanOldScreenshots();
        // 重置定时器倒计时：从此刻起重新计时
        _timer?.Change(TimeSpan.FromMinutes(_intervalMinutes), TimeSpan.FromMinutes(_intervalMinutes));
    }

    /// <summary>
    /// 截屏并保存到磁盘。用 GetSystemMetrics 拿屏幕尺寸，GDI 截屏，
    /// 支持 JPG（可调质量）和 PNG 两种格式，保存后写一条数据库记录。
    /// </summary>
    private void CaptureAndSave()
    {
        try
        {
            // 用 Win32 API 获取屏幕分辨率
            int width = GetSystemMetrics(SM_CXSCREEN);
            int height = GetSystemMetrics(SM_CYSCREEN);

            // GDI 截屏：创建 Bitmap + CopyFromScreen
            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
            }

            // 根据设置选择保存格式
            string format = SettingsRepository.Get("ScreenshotFormat", "jpg") ?? "jpg";
            string ext = format == "png" ? "png" : "jpg";
            string fileName = $"screenshot_{DateTime.Now:HH-mm-ss}.{ext}";
            // 按日期分文件夹：screenshots/2026-08-15/screenshot_14-30-00.jpg
            string dateDir = Path.Combine(_screenshotDir, DateTime.Now.ToDateKey());
            Directory.CreateDirectory(dateDir);
            string filePath = Path.Combine(dateDir, fileName);

            if (format == "png")
            {
                bmp.Save(filePath, ImageFormat.Png);
            }
            else
            {
                // JPG 模式：根据设置调整压缩质量（high=80, medium=50, low=30）
                var encoderParams = new EncoderParameters(1);
                var quality = SettingsRepository.Get("ScreenshotQuality", "medium") switch
                {
                    "high" => 80L,
                    "low" => 30L,
                    _ => 50L
                };
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                bmp.Save(filePath, jpegEncoder, encoderParams);
            }

            // 存数据库：如果是程序目录下的路径就用相对路径，否则用绝对路径
            var fileSize = new FileInfo(filePath).Length;
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string dbPath = filePath.StartsWith(appDir) ? filePath.Substring(appDir.Length) : filePath;
            ScreenshotRepository.Insert(dbPath, fileSize);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error($"截图保存失败：路径「{_screenshotDir}」无写入权限，请在设置中修改截图保存路径", ex);
        }
        catch (Exception ex)
        {
            Logger.Error("截图保存失败", ex);
        }
    }

    /// <summary>
    /// 查找指定图片格式对应的编码器（JPG/PNG 等）。
    /// </summary>
    /// <param name="format">目标图片格式</param>
    /// <returns>对应的编码器信息</returns>
    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid)
                return codec;
        }
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
        return ScreenshotRepository.GetForTimeRange(startTime, endTime);
    }
}
