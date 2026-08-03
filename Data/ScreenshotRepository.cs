using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 截图记录仓储 — 负责 Screenshots 表的增删查
/// </summary>
public static class ScreenshotRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

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
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@CapturedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 获取某个时间点最近的一张截图路径（自动拼接相对路径）
    /// </summary>
    public static string? GetForTime(DateTime time)
    {
        EnsureInit();
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
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
