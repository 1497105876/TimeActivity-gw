using System;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;

namespace TimeActivity.Data;

/// <summary>
/// AI 总结仓储 — 负责 AISummaries 表的增删查
/// </summary>
public static class AISummaryRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    public static void Insert(DateTime date, string summaryText, string summaryType = "daily", string autoType = "manual")
    {
        EnsureInit();

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();

        // 先删同类型同日期同来源的旧记录，再插入新的
        using var delCmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE Date=@Date AND SummaryType=@SummaryType AND AutoType=@AutoType", conn);
        delCmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        delCmd.Parameters.AddWithValue("@SummaryType", summaryType);
        delCmd.Parameters.AddWithValue("@AutoType", autoType);
        delCmd.ExecuteNonQuery();

        const string sql = @"INSERT INTO AISummaries (Date, SummaryText, SummaryType, AutoType, CreatedAt)
            VALUES (@Date, @SummaryText, @SummaryType, @AutoType, @CreatedAt)";

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@SummaryText", summaryText);
        cmd.Parameters.AddWithValue("@SummaryType", summaryType);
        cmd.Parameters.AddWithValue("@AutoType", autoType);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        cmd.ExecuteNonQuery();
    }

    public static bool HasAuto(DateTime date, string summaryType)
    {
        EnsureInit();
        const string sql = "SELECT COUNT(*) FROM AISummaries WHERE Date=@Date AND SummaryType=@Type AND AutoType='auto'";
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Type", summaryType);
        long count = (long)cmd.ExecuteScalar()!;
        return count > 0;
    }

    public static string? Get(DateTime date, string summaryType = "daily")
    {
        EnsureInit();
        const string sql = "SELECT SummaryText FROM AISummaries WHERE Date = @Date AND SummaryType = @Type ORDER BY CreatedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Type", summaryType);

        var result = cmd.ExecuteScalar();
        return result as string;
    }

    /// <summary>
    /// 获取 AI 总结（带 AutoType 过滤），返回 (内容, 生成时间)
    /// </summary>
    public static (string? summary, string? createdAt) GetWithMeta(DateTime date, string summaryType, string autoType)
    {
        EnsureInit();
        const string sql = "SELECT SummaryText, CreatedAt FROM AISummaries WHERE Date = @Date AND SummaryType = @Type AND AutoType = @Auto ORDER BY CreatedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Type", summaryType);
        cmd.Parameters.AddWithValue("@Auto", autoType);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            string text = reader.GetString(0);
            string? createdAt = reader.IsDBNull(1) ? null : reader.GetString(1);
            return (text, createdAt);
        }
        return (null, null);
    }
}
