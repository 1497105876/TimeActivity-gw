// ============================================================================
// Logger.cs — 简易文件日志（静态类）
// 职责：Info/Error 统一入口；按天写 logs/log_yyyy-MM-dd.txt；
//       内部加锁保证多线程写入安全；失败静默避免日志引发次生异常。
// ============================================================================
using System;
using System.IO;
using System.Text;

namespace TimeActivity.Services;

/// <summary>
/// 简单文件日志，写到程序目录 logs/ 下
/// </summary>
public static class Logger
{
    // 日志写入锁，防止多线程同时写文件
    private static readonly object _lock = new();
    // 日志目录路径（静态构造中初始化；拿不到时为空串 = 日志功能失效但不崩溃）
    private static string _logDir = "";

    // 静态构造：初始化日志目录，程序目录下 logs/ 文件夹
    // 静态构造由 CLR 保证线程安全且只执行一次
    static Logger()
    {
        try
        {
            // BaseDirectory = exe 所在目录（对单实例桌面程序即安装目录）
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            // 目录不存在才创建；已存在时 Exists 为 true 跳过
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);
        }
        catch (Exception)
        {
            // 日志目录创建失败时用系统临时目录兜底
            // （常见原因：安装在无写权限目录，如 Program Files）
            try { _logDir = Path.GetTempPath(); }
            catch { _logDir = ""; } // 临时目录都拿不到，日志功能失效但不崩溃
        }
    }

    /// <summary>
    /// 记录 INFO 级别日志。
    /// </summary>
    /// <param name="message">日志内容</param>
    public static void Info(string message)
    {
        Write("INFO", message);
    }

    /// <summary>
    /// 记录 WARN 级别日志。
    /// </summary>
    /// <param name="message">日志内容</param>
    public static void Warning(string message)
    {
        Write("WARN", message);
    }

    /// <summary>
    /// 记录 ERROR 级别日志，可附带异常对象（包含类型名、消息、堆栈）。
    /// </summary>
    /// <param name="message">错误描述</param>
    /// <param name="ex">异常对象，可为 null</param>
    public static void Error(string message, Exception? ex = null)
    {
        // 拼接错误信息：描述 + 异常类型 + 异常消息 + 堆栈
        var sb = new StringBuilder();
        sb.Append(message);
        if (ex != null)
        {
            // 用 " | " 分隔描述与异常摘要，便于日志检索/切分
            sb.Append(" | ");
            sb.Append(ex.GetType().Name);
            sb.Append(": ");
            sb.Append(ex.Message);
            if (ex.StackTrace != null)
            {
                // 堆栈含换行 → 该条日志占多行（按行解析日志时需注意）
                sb.Append("\n");
                sb.Append(ex.StackTrace);
            }
        }
        Write("ERROR", sb.ToString());
    }

    /// <summary>
    /// 实际写日志到文件的核心方法。按天分文件（如 log_2026-08-14.txt），线程安全。
    /// </summary>
    /// <param name="level">日志级别（INFO/WARN/ERROR）</param>
    /// <param name="message">日志内容</param>
    private static void Write(string level, string message)
    {
        try
        {
            // 整个写流程包在 try 里：任何 IO 失败都不能向上抛，
            // 否则"记录错误"这个动作本身会引发次生异常
            // 文件名按天命名
            var fileName = $"log_{DateTime.Now:yyyy-MM-dd}.txt";
            var filePath = Path.Combine(_logDir, fileName);
            // 每行格式：[时分秒] [级别] 内容
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}\n";
            // 加锁保证多线程写入不会交错
            lock (_lock)
            {
                // 每条日志独立打开-追加-关闭：简单可靠、崩溃不丢缓冲，
                // 但高频调用时有 IO 开销（当前采样频率下可接受）
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch { /* 写日志失败不能抛异常，静默忽略 */ }
    }
}
