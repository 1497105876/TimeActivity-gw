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
/// 简单文件日志，写到程序目录 logs/ 下（按天一个文件，无大小滚动、无自动清理）。
/// </summary>
/// <remarks>
/// 设计取舍：只做"够用"的落盘日志 —— 无第三方依赖、无配置、无异步队列，
/// 调用方任意线程直接调 Info/Warning/Error 即可，内部靠 <see cref="_lock"/> 串行化。
/// 已知限制（当前版本未实现，见文末风险清单）：
/// 1) 不按大小切分、不压缩、不删除历史文件，长期运行只靠"按天分文件"天然限速；
/// 2) 目录初始化失败会退到系统临时目录，极端情况下两者都失败则日志静默失效；
/// 3) 写失败一律静默，不向上抛，避免"记日志"这个动作本身引发次生异常。
/// </remarks>
public static class Logger
{
    // 日志写入锁，防止多线程同时写文件
    /// <summary>写文件互斥锁：所有线程的 Info/Warning/Error 都经它串行化，防止整行内容被交错写坏。</summary>
    private static readonly object _lock = new();
    // 日志目录路径（静态构造中初始化；拿不到时为空串 = 日志功能失效但不崩溃）
    /// <summary>日志目录：静态构造里初始化为 exe 目录下 logs/；建目录失败退回系统临时目录，再失败置空串。</summary>
    private static string _logDir = "";

    // 静态构造：初始化日志目录，程序目录下 logs/ 文件夹
    // 静态构造由 CLR 保证线程安全且只执行一次
    static Logger()
    {
        // 目录初始化失败不能让它击穿静态构造（会导致整个类首次使用时抛 TypeInitializationException）
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
        // 转交统一的写入方法，级别固定为 INFO
        Write("INFO", message);
    }

    /// <summary>
    /// 记录 WARN 级别日志。
    /// </summary>
    /// <param name="message">日志内容</param>
    public static void Warning(string message)
    {
        // 转交统一的写入方法，级别固定为 WARN
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
        // 用 StringBuilder 拼装：带堆栈时文本很长且要分段追加，避免反复产生临时字符串
        var sb = new StringBuilder();
        // 先放调用方给的语义描述：不传异常时这一句就是最终日志内容
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
        // 拼好的整段文本交给 Write，级别为 ERROR
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
            // 文件名按天命名（yyyy-MM-dd 为本地日期；跨零点后自然写到新文件，等效按天轮转、无需手动清理）
            var fileName = $"log_{DateTime.Now:yyyy-MM-dd}.txt";
            // 拼出完整路径（_logDir 为空串时 Path.Combine 退化为相对当前目录，通常仍能写成功）
            var filePath = Path.Combine(_logDir, fileName);
            // 每行格式：[时分秒] [级别] 内容
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}\n";
            // 加锁保证多线程写入不会交错
            lock (_lock)
            {
                // 每条日志独立打开-追加-关闭：简单可靠、崩溃不丢缓冲，
                // 但高频调用时有 IO 开销（当前采样频率下可接受）
                // UTF8 无 BOM：与常见文本查看器/日志采集工具兼容
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch { /* 写日志失败不能抛异常，静默忽略 */ }
    }
}
