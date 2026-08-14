using System;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;

namespace TimeActivity.Data;

/// <summary>
/// AI 总结仓储 — 负责 AISummaries 表的增删查
/// </summary>
public static class AISummaryRepository
{
    // 确保数据库已初始化
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 插入一条 AI 总结记录（同日期同类型同来源的旧记录会被先删再插）
    /// </summary>
    /// <param name="date">总结对应的日期</param>
    /// <param name="summaryText">总结正文内容</param>
    /// <param name="summaryType">总结类型，默认 daily（每日总结）</param>
    /// <param name="autoType">来源类型：manual（手动生成）或 auto（自动生成）</param>
    public static void Insert(DateTime date, string summaryText, string summaryType = "daily", string autoType = "manual")
    {
        EnsureInit();

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();

        // 先删同类型同日期同来源的旧记录，再插入新的，保证每次只保留最新一条
        using var delCmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE Date=@Date AND SummaryType=@SummaryType AND AutoType=@AutoType", conn);
        delCmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        delCmd.Parameters.AddWithValue("@SummaryType", summaryType);
        delCmd.Parameters.AddWithValue("@AutoType", autoType);
        delCmd.ExecuteNonQuery();

        const string sql = @"INSERT INTO AISummaries (Date, SummaryText, SummaryType, AutoType, CreatedAt)
            VALUES (@Date, @SummaryText, @SummaryType, @AutoType, @CreatedAt)";

        using var cmd = new SqliteCommand(sql, conn);
        // 日期统一用 yyyy-MM-dd 格式存储
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@SummaryText", summaryText);
        cmd.Parameters.AddWithValue("@SummaryType", summaryType);
        cmd.Parameters.AddWithValue("@AutoType", autoType);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 检查某天是否已有自动生成的 AI 总结
    /// </summary>
    /// <param name="date">要检查的日期</param>
    /// <param name="summaryType">总结类型（如 daily）</param>
    /// <returns>已存在自动总结返回 true，否则 false</returns>
    public static bool HasAuto(DateTime date, string summaryType)
    {
        EnsureInit();
        // 查 AutoType='auto' 的记录数量，大于 0 说明已有自动总结
        const string sql = "SELECT COUNT(*) FROM AISummaries WHERE Date=@Date AND SummaryType=@Type AND AutoType='auto'";
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Type", summaryType);
        long count = (long)cmd.ExecuteScalar()!;
        return count > 0;
    }

    /// <summary>
    /// 获取某天最新一条 AI 总结文本（不限来源类型）
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <param name="summaryType">总结类型，默认 daily</param>
    /// <returns>总结文本，没有则返回 null</returns>
    public static string? Get(DateTime date, string summaryType = "daily")
    {
        EnsureInit();
        // 按创建时间降序取第一条，即最新的一条
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
