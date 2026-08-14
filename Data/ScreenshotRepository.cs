using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 截图记录仓储 — 负责 Screenshots 表的增删查
/// </summary>
public static class ScreenshotRepository
{
    // 确保数据库已初始化
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 插入一条截图记录，返回新记录的自增 Id
    /// </summary>
    /// <param name="filePath">截图文件路径（相对路径或绝对路径）</param>
    /// <param name="fileSize">文件大小（字节）</param>
    /// <returns>新插入记录的自增 Id</returns>
    public static long Insert(string filePath, long fileSize)
    {
        EnsureInit();
        const string sql = @"
            INSERT INTO Screenshots (FilePath, CapturedAt, FileSize, CreatedAt)
            VALUES (@FilePath, @CapturedAt, @FileSize, @CreatedAt);
            SELECT last_insert_rowid();";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // CapturedAt 和 CreatedAt 都用当前时间，精确到毫秒
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@CapturedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 获取某个时间点之前最近的一张截图路径（用于活动回溯）
    /// </summary>
    /// <param name="time">目标时间点</param>
    /// <returns>截图文件的绝对路径，没有则返回 null</returns>
    public static string? GetForTime(DateTime time)
    {
        EnsureInit();
        // 查捕获时间 <= 指定时间的最近一张截图
        const string sql = @"
            SELECT FilePath FROM Screenshots
            WHERE CapturedAt <= @Time
            ORDER BY CapturedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Time", time.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        var result = cmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
        {
            string path = (string)result;
            // 相对路径拼接程序目录，绝对路径直接使用
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            // 文件不存在则返回 null（可能已被清理）
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
