using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;
using TimeActivity.Helpers;

namespace TimeActivity.Data;

/// <summary>
/// 每日汇总仓储 — 预聚合三张表：DailyTotal / DailyCategorySummary / DailyProcessSummary
/// 生成时机：程序启动时补昨天 + 每天 23:59 自动生成当天
/// 查询时统计页读汇总表而非 Activities 原始表，大幅减少扫描行数
/// </summary>
public static class DailySummaryRepository
{
    // 确保数据库已初始化
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 扫描 Activities 表，找出有数据但 DailyTotal 里没记录的日期，全部补生成
    /// </summary>
    public static void GenerateAllMissing()
    {
        using var conn = DbAccess.Open();

        // 找出 Activities 里有但 DailyTotal 里没有的日期——即缺失汇总的日期
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT date(StartTime) as D 
            FROM Activities 
            WHERE date(StartTime) NOT IN (SELECT Date FROM DailyTotal)
            ORDER BY D";
        var missingDates = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            missingDates.Add(reader.GetString(0));
        reader.Close();

        // 逐天补生成汇总，复用同一连接避免每天重开，失败只记日志不中断
        foreach (var d in missingDates)
        {
            try { GenerateForDate(d, conn, null); }
            catch (Exception ex) { Logger.Error($"补生成汇总失败: {d}", ex); }
        }

        if (missingDates.Count > 0)
            Logger.Info($"已补生成 {missingDates.Count} 天的汇总数据");
    }

    /// <summary>
    /// 生成某天的汇总数据（写入三张表：DailyTotal / DailyCategorySummary / DailyProcessSummary）
    /// </summary>
    /// <param name="date">日期字符串，格式 yyyy-MM-dd</param>
    public static void GenerateForDate(string date)
    {
        using var conn = DbAccess.Open();
        GenerateForDate(date, conn, null);
    }

    /// <summary>
    /// 生成某天的汇总数据（可在外部事务内执行，保证原子性）
    /// </summary>
    /// <param name="date">日期字符串，格式 yyyy-MM-dd</param>
    /// <param name="conn">已打开的数据库连接</param>
    /// <param name="transaction">外部事务（可选），传入则在该事务内执行</param>
    public static void GenerateForDate(string date, SqliteConnection conn, SqliteTransaction? transaction = null)
    {
        // 1. DailyTotal — 计算当天总时长和活跃时长（排除空闲）
        using var totalCmd = conn.CreateCommand();
        if (transaction != null) totalCmd.Transaction = transaction;
        // COALESCE 防 NULL，第二个 SUM 用 CASE WHEN 过滤空闲时长
        totalCmd.CommandText = "SELECT COALESCE(SUM(Duration),0), COALESCE(SUM(CASE WHEN IsIdle=0 THEN Duration ELSE 0 END),0) FROM Activities WHERE date(StartTime)=@date";
        totalCmd.Parameters.AddWithValue("@date", date);
        using var totalReader = totalCmd.ExecuteReader();
        long totalSeconds = 0, totalActive = 0;
        if (totalReader.Read())
        {
            totalSeconds = totalReader.GetInt64(0);
            totalActive = totalReader.GetInt64(1);
        }
        totalReader.Close();

        // UPSERT：日期已存在则更新，否则插入
        using var upsertTotal = conn.CreateCommand();
        if (transaction != null) upsertTotal.Transaction = transaction;
        upsertTotal.CommandText = @"INSERT INTO DailyTotal (Date, TotalActiveSeconds, TotalSeconds)
            VALUES (@date, @active, @total)
            ON CONFLICT(Date) DO UPDATE SET TotalActiveSeconds=@active, TotalSeconds=@total, CreatedAt=datetime('now','localtime')";
        upsertTotal.Parameters.AddWithValue("@date", date);
        upsertTotal.Parameters.AddWithValue("@active", totalActive);
        upsertTotal.Parameters.AddWithValue("@total", totalSeconds);
        upsertTotal.ExecuteNonQuery();

        // 2. DailyCategorySummary — 按类别汇总（先删旧数据再插入新的）
        using var delCat = conn.CreateCommand();
        if (transaction != null) delCat.Transaction = transaction;
        delCat.CommandText = "DELETE FROM DailyCategorySummary WHERE Date=@date";
        delCat.Parameters.AddWithValue("@date", date);
        delCat.ExecuteNonQuery();

        using var catCmd = conn.CreateCommand();
        if (transaction != null) catCmd.Transaction = transaction;
        // 按分类汇总时长，只统计非空闲记录
        catCmd.CommandText = @"SELECT Category, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTime)=@date AND IsIdle=0 GROUP BY Category";
        catCmd.Parameters.AddWithValue("@date", date);
        // 先读到内存，再批量写入（避免 reader 打开时执行命令导致 SQLite 报错）
        var catRows = new List<(string cat, long sec)>();
        using (var catReader = catCmd.ExecuteReader())
        {
            while (catReader.Read())
                catRows.Add((catReader.GetString(0), catReader.GetInt64(1)));
        }
        foreach (var (cat, sec) in catRows)
        {
            using var insCat = conn.CreateCommand();
            if (transaction != null) insCat.Transaction = transaction;
            insCat.CommandText = "INSERT INTO DailyCategorySummary (Date, Category, Seconds) VALUES (@d, @c, @s)";
            insCat.Parameters.AddWithValue("@d", date);
            insCat.Parameters.AddWithValue("@c", cat);
            insCat.Parameters.AddWithValue("@s", sec);
            insCat.ExecuteNonQuery();
        }

        // 3. DailyProcessSummary — 按进程汇总
        using var delProc = conn.CreateCommand();
        if (transaction != null) delProc.Transaction = transaction;
        delProc.CommandText = "DELETE FROM DailyProcessSummary WHERE Date=@date";
        delProc.Parameters.AddWithValue("@date", date);
        delProc.ExecuteNonQuery();

        using var procCmd = conn.CreateCommand();
        if (transaction != null) procCmd.Transaction = transaction;
        // 按进程名+分类汇总，一个进程可能出现多个分类，取时长最长的那个
        procCmd.CommandText = @"SELECT ProcessName, Category, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTime)=@date AND IsIdle=0 GROUP BY ProcessName, Category
            ORDER BY ProcessName, Total DESC";
        procCmd.Parameters.AddWithValue("@date", date);
        // 同样先读到内存，同进程取时长最长的类别（主键是 Date+ProcessName）
        var procRows = new List<(string proc, string cat, long sec)>();
        using (var procReader = procCmd.ExecuteReader())
        {
            while (procReader.Read())
                procRows.Add((procReader.GetString(0), procReader.GetString(1), procReader.GetInt64(2)));
        }
        // 同进程只保留时长最大的那条（SQL 已按 Total DESC 排序，第一个就是最大的）
        var seen = new HashSet<string>();
        foreach (var (proc, cat, sec) in procRows)
        {
            if (!seen.Add(proc)) continue; // 已有该进程，跳过
            using var insProc = conn.CreateCommand();
            if (transaction != null) insProc.Transaction = transaction;
            insProc.CommandText = "INSERT INTO DailyProcessSummary (Date, ProcessName, Category, Seconds) VALUES (@d, @p, @c, @s)";
            insProc.Parameters.AddWithValue("@d", date);
            insProc.Parameters.AddWithValue("@p", proc);
            insProc.Parameters.AddWithValue("@c", cat);
            insProc.Parameters.AddWithValue("@s", sec);
            insProc.ExecuteNonQuery();
        }

        Logger.Info($"每日汇总已生成：{date}，总活跃 {totalActive} 秒");
    }

    /// <summary>
    /// 查询日期范围内的每日总活跃时长（趋势图用）
    /// </summary>
    public static Dictionary<string, int> GetDailyTotals(DateTime start, DateTime end, bool includeIdle = false)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string col = includeIdle ? "TotalSeconds" : "TotalActiveSeconds";
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            $"SELECT Date, {col} FROM DailyTotal WHERE Date >= @Start AND Date <= @End ORDER BY Date", conn);
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 查询日期范围内按类别汇总（类别占比用）
    /// </summary>
    public static Dictionary<string, int> GetCategorySummary(DateTime start, DateTime end)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            @"SELECT Category, SUM(Seconds) as Total FROM DailyCategorySummary 
              WHERE Date >= @Start AND Date <= @End GROUP BY Category ORDER BY Total DESC", conn);
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 查询日期范围内按进程汇总（Top应用用）
    /// </summary>
    public static Dictionary<string, int> GetProcessSummary(DateTime start, DateTime end, string? categoryFilter = null)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string filter = string.IsNullOrEmpty(categoryFilter) ? "" : " AND Category=@Cat";
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            $@"SELECT ProcessName, SUM(Seconds) as Total FROM DailyProcessSummary 
              WHERE Date >= @Start AND Date <= @End{filter} GROUP BY ProcessName ORDER BY Total DESC", conn);
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());
        if (!string.IsNullOrEmpty(categoryFilter))
            cmd.Parameters.AddWithValue("@Cat", categoryFilter);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }
}
