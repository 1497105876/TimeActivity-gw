using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using TimeActivity.Services;
using System.Runtime.InteropServices;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// 截图服务 — 定时截屏 + 切换应用时截屏，仿 ManicTime
/// </summary>
public class ScreenshotService
{
    private int _intervalMinutes;
    private string _screenshotDir = "";
    private System.Threading.Timer? _timer;
    private bool _running;

    // 是否在切换应用时截屏
    private bool _captureOnSwitch;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public bool IsRunning => _running;

    public ScreenshotService()
    {
        ReloadSettings();
    }

    /// <summary>
    /// 从数据库重新读取设置
    /// </summary>
    public void ReloadSettings()
    {
        _intervalMinutes = int.TryParse(
            SettingsRepository.Get("ScreenshotIntervalMinutes", "5"), out int iv) && iv > 0
            ? Math.Clamp(iv, 1, 1440) : 5;

        _captureOnSwitch = SettingsRepository.Get("ScreenshotOnSwitch", "true") == "true";

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

    public void Start()
    {
        if (_running) return;
        // 检查设置开关 — 没开截图就不启动
        if (SettingsRepository.Get("EnableScreenshot", "false") != "true") return;
        ReloadSettings();
        _running = true;

        CaptureAndSave();
        CleanOldScreenshots();

        // 定时截屏
        _timer = new System.Threading.Timer(
            _ => { CaptureAndSave(); CleanOldScreenshots(); },
            null,
            TimeSpan.FromMinutes(_intervalMinutes),
            TimeSpan.FromMinutes(_intervalMinutes));
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 清理旧截图 — 仿 ManicTime 存储限制
    /// </summary>
    private void CleanOldScreenshots()
    {
        try
        {
            if (!Directory.Exists(_screenshotDir)) return;

            bool enableMaxSize = SettingsRepository.Get("EnableMaxSize", "true") == "true";
            bool enableMaxAge = SettingsRepository.Get("EnableMaxAge", "true") == "true";

            var files = Directory.GetFiles(_screenshotDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按最大年龄清理
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

            // 按最大总大小清理（删最老的）
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
    /// 切换应用时调用 — 仿 ManicTime "在每次应用程序切换时截屏"
    /// 切换后重置定时器倒计时
    /// </summary>
    public void OnAppSwitched()
    {
        if (!_running || !_captureOnSwitch) return;
        CaptureAndSave();
        CleanOldScreenshots();
        // 重置定时器倒计时：从此刻起重新计时
        _timer?.Change(TimeSpan.FromMinutes(_intervalMinutes), TimeSpan.FromMinutes(_intervalMinutes));
    }

    private void CaptureAndSave()
    {
        try
        {
            int width = GetSystemMetrics(SM_CXSCREEN);
            int height = GetSystemMetrics(SM_CYSCREEN);

            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
            }

            string format = SettingsRepository.Get("ScreenshotFormat", "jpg") ?? "jpg";
            string ext = format == "png" ? "png" : "jpg";
            string fileName = $"screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}";
            string filePath = Path.Combine(_screenshotDir, fileName);

            if (format == "png")
            {
                bmp.Save(filePath, ImageFormat.Png);
            }
            else
            {
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

            var fileSize = new FileInfo(filePath).Length;
            // 数据库存相对路径（相对于程序目录）
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

    public static int GetScreenWidth() => GetSystemMetrics(SM_CXSCREEN);
    public static int GetScreenHeight() => GetSystemMetrics(SM_CYSCREEN);

    /// <summary>
    /// 获取某个时间点最近的一张截图（委托 DatabaseHelper 查询）
    /// </summary>
    public static string? GetScreenshotForTime(DateTime time)
    {
        return ScreenshotRepository.GetForTime(time);
    }
}
