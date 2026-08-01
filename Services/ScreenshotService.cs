using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
        _intervalMinutes = int.Parse(
            DatabaseHelper.GetSetting("ScreenshotIntervalMinutes", "5") ?? "5");

        _captureOnSwitch = DatabaseHelper.GetSetting("ScreenshotOnSwitch", "true") == "true";

        string? userPath = DatabaseHelper.GetSetting("ScreenshotPath", "");
        if (!string.IsNullOrWhiteSpace(userPath))
            _screenshotDir = userPath;
        else
            _screenshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");

        Directory.CreateDirectory(_screenshotDir);
    }

    public void Start()
    {
        if (_running) return;
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

            bool enableMaxSize = DatabaseHelper.GetSetting("EnableMaxSize", "true") == "true";
            bool enableMaxAge = DatabaseHelper.GetSetting("EnableMaxAge", "true") == "true";

            var files = Directory.GetFiles(_screenshotDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // 按最大年龄清理
            if (enableMaxAge && int.TryParse(DatabaseHelper.GetSetting("MaxScreenshotAgeDays", "30"), out int maxAge))
            {
                var cutoff = DateTime.Now.AddDays(-maxAge);
                foreach (var f in files)
                {
                    if (f.CreationTime < cutoff)
                    {
                        try { f.Delete(); } catch { }
                    }
                }
                files = files.Where(f => f.Exists).ToList();
            }

            // 按最大总大小清理（删最老的）
            if (enableMaxSize && int.TryParse(DatabaseHelper.GetSetting("MaxScreenshotSizeMB", "5120"), out int maxMB))
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
                    catch { }
                    files.RemoveAt(0);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 切换应用时调用 — 仿 ManicTime "在每次应用程序切换时截屏"
    /// </summary>
    public void OnAppSwitched()
    {
        if (!_running || !_captureOnSwitch) return;
        CaptureAndSave();
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

            string format = DatabaseHelper.GetSetting("ScreenshotFormat", "jpg") ?? "jpg";
            string ext = format == "png" ? "png" : "jpg";
            string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
            string filePath = Path.Combine(_screenshotDir, fileName);

            if (format == "png")
            {
                bmp.Save(filePath, ImageFormat.Png);
            }
            else
            {
                var encoderParams = new EncoderParameters(1);
                var quality = DatabaseHelper.GetSetting("ScreenshotQuality", "medium") switch
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
            DatabaseHelper.InsertScreenshot(filePath, fileSize);
        }
        catch
        {
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
    /// 获取某个时间点最近的一张截图
    /// </summary>
    public static string? GetScreenshotForTime(DateTime time)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db")}");
            conn.Open();
            using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(@"
                SELECT FilePath FROM Screenshots
                WHERE CapturedAt <= @Time
                ORDER BY CapturedAt DESC LIMIT 1", conn);
            cmd.Parameters.AddWithValue("@Time", time.ToString("yyyy-MM-dd HH:mm:ss.fff"));

            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                string path = (string)result;
                if (File.Exists(path))
                    return path;
            }
        }
        catch { }
        return null;
    }
}
