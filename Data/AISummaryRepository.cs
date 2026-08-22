// ============================================================================
// AISummaryRepository.cs — AISummaries 表的仓储（静态类）
// 职责：AI 总结的写入/查询/存在性检查；manual 与 auto 双来源管理；
//       InvalidateRecent 使近期总结失效以触发重算。
// 依赖唯一索引 UX_AISummaries_Type(Date,SummaryType,AutoType) 防重复。
// ============================================================================
using System;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;
using TimeActivity.Helpers;

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

        using var conn = DbAccess.Open();

        // 用事务包住 DELETE+INSERT，中途失败不会丢数据
        using var transaction = conn.BeginTransaction();

        // 先删同类型同日期同来源的旧记录，再插入新的，保证每次只保留最新一条
        using var delCmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE Date=@Date AND SummaryType=@SummaryType AND AutoType=@AutoType", conn, transaction);
        delCmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        delCmd.Parameters.AddWithValue("@SummaryType", summaryType);
        delCmd.Parameters.AddWithValue("@AutoType", autoType);
        delCmd.ExecuteNonQuery();

        const string sql = @"INSERT INTO AISummaries (Date, SummaryText, SummaryType, AutoType, CreatedAt)
            VALUES (@Date, @SummaryText, @SummaryType, @AutoType, @CreatedAt)";

        using var cmd = new SqliteCommand(sql, conn, transaction);
        // 日期统一用 yyyy-MM-dd 格式存储
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        cmd.Parameters.AddWithValue("@SummaryText", summaryText);
        cmd.Parameters.AddWithValue("@SummaryType", summaryType);
        cmd.Parameters.AddWithValue("@AutoType", autoType);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        cmd.ExecuteNonQuery();
        transaction.Commit();
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
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        cmd.Parameters.AddWithValue("@Type", summaryType);
        long count = (long)cmd.ExecuteScalar()!;
        return count > 0;
    }

    /// <summary>
    /// 使"近期"的自动总结失效（删除 auto 来源记录），下一次 GenerateMissingAsync 会重新生成。
    /// 用于底层活动数据被修改（如重新分类）后让已有总结刷新。仅删 auto，保留用户手动总结。
    /// 覆盖范围：最近 7 天日报 + 最近一个完整周 + 最近一个完整月（与 SummaryScheduler 的补算窗口一致）。
    /// </summary>
    public static void InvalidateRecent()
    {
        EnsureInit();
        var today = DateTime.Today;
        using var conn = DbAccess.Open();

        // 删最近 7 天日报（auto）
        using (var cmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE AutoType='auto' AND SummaryType='daily' AND Date >= @From", conn))
        {
            cmd.Parameters.AddWithValue("@From", today.AddDays(-7).ToDateKey());
            cmd.ExecuteNonQuery();
        }
        // 删最近一个完整周（auto）
        using (var cmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE AutoType='auto' AND SummaryType='weekly' AND Date=@Ws", conn))
        {
            cmd.Parameters.AddWithValue("@Ws", DateHelper.GetLatestClosedWeekStart().ToDateKey());
            cmd.ExecuteNonQuery();
        }
        // 删最近一个完整月（auto）
        using (var cmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE AutoType='auto' AND SummaryType='monthly' AND Date=@Ms", conn))
        {
            cmd.Parameters.AddWithValue("@Ms", DateHelper.GetLatestClosedMonthStart().ToDateKey());
            cmd.ExecuteNonQuery();
        }
        Logger.Info("已使近期 AI 自动总结失效，将在下次检查时重新生成");
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

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
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

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
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
