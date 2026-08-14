using System;
using System.IO;
using System.Text;

namespace TimeActivity.Services;

/// <summary>
/// 简单文件日志，写到程序目录 logs/ 下
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string _logDir = "";

    static Logger()
    {
        try
        {
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);
        }
        catch (Exception)
        {
            // 日志目录创建失败时用临时目录兜底
            try { _logDir = Path.GetTempPath(); }
            catch { _logDir = ""; } // 临时目录都拿不到，日志功能失效但不崩溃
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warning(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder();
        sb.Append(message);
        if (ex != null)
        {
            sb.Append(" | ");
            sb.Append(ex.GetType().Name);
            sb.Append(": ");
            sb.Append(ex.Message);
            if (ex.StackTrace != null)
            {
                sb.Append("\n");
                sb.Append(ex.StackTrace);
            }
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            var fileName = $"log_{DateTime.Now:yyyy-MM-dd}.txt";
            var filePath = Path.Combine(_logDir, fileName);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}\n";
            lock (_lock)
            {
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch { /* 写日志失败不能抛异常，静默忽略 */ }
    }
}
