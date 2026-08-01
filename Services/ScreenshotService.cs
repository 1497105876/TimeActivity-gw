using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using TimeActivity.Data;

namespace TimeActivity.Services;

/// <summary>
/// 截图服务 — 定时截屏，存到用户指定文件夹 + 数据库
/// </summary>
public class ScreenshotService
{
    private int _intervalMinutes;
    private string _screenshotDir = "";
    private System.Threading.Timer? _timer;
    private bool _running;

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
    /// 从数据库重新读取设置（改了设置后调用）
    /// </summary>
    public void ReloadSettings()
    {
        _intervalMinutes = int.Parse(
            DatabaseHelper.GetSetting("ScreenshotIntervalMinutes", "5") ?? "5");

        // 读用户设置的路径，空则用 exe 目录下 screenshots
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
        ReloadSettings(); // 启动时重新读设置
        _running = true;

        CaptureAndSave();

        _timer = new System.Threading.Timer(
            _ => CaptureAndSave(),
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

            // 命名：screenshot_年月日_时分秒.jpg
            string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            string filePath = Path.Combine(_screenshotDir, fileName);

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
