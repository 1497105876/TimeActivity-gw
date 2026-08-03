using System.Text;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;

namespace TimeActivity.Data;

/// <summary>
/// 每日汇总仓储 — 负责 DailySummaries 表的生成和查询
/// </summary>
public static class DailySummaryRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 生成某天的每日汇总（UPSERT 到 DailySummaries 表）
    /// </summary>
    public static void GenerateForDate(string date)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();

        // 计算总活跃时长
        using var totalCmd = conn.CreateCommand();
        totalCmd.CommandText = "SELECT COALESCE(SUM(Duration),0) FROM Activities WHERE date(StartTime)=@date AND IsIdle=0";
        totalCmd.Parameters.AddWithValue("@date", date);
        long totalActive = (long)totalCmd.ExecuteScalar()!;

        // 计算分类细分
        using var catCmd = conn.CreateCommand();
        catCmd.CommandText = @"SELECT Category, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTime)=@date AND IsIdle=0 GROUP BY Category ORDER BY Total DESC";
        catCmd.Parameters.AddWithValue("@date", date);
        var catBreakdown = new StringBuilder();
        using (var reader = catCmd.ExecuteReader())
        {
            bool first = true;
            while (reader.Read())
            {
                if (!first) catBreakdown.Append(";");
                catBreakdown.Append($"{reader.GetString(0)}:{reader.GetInt64(1)}");
                first = false;
            }
        }

        // 计算 Top 应用
        using var appCmd = conn.CreateCommand();
        appCmd.CommandText = @"SELECT ProcessName, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTime)=@date AND IsIdle=0 GROUP BY ProcessName ORDER BY Total DESC LIMIT 5";
        appCmd.Parameters.AddWithValue("@date", date);
        var topApps = new StringBuilder();
        using (var reader = appCmd.ExecuteReader())
        {
            bool first = true;
            while (reader.Read())
            {
                if (!first) topApps.Append(";");
                topApps.Append($"{reader.GetString(0)}:{reader.GetInt64(1)}");
                first = false;
            }
        }

        // UPSERT
        using var upsertCmd = conn.CreateCommand();
        upsertCmd.CommandText = @"INSERT INTO DailySummaries (Date, TotalActiveTime, CategoryBreakdown, TopApps)
            VALUES (@date, @total, @cat, @apps)
            ON CONFLICT(Date) DO UPDATE SET TotalActiveTime=@total, CategoryBreakdown=@cat, TopApps=@apps, CreatedAt=datetime('now','localtime')";
        upsertCmd.Parameters.AddWithValue("@date", date);
        upsertCmd.Parameters.AddWithValue("@total", totalActive);
        upsertCmd.Parameters.AddWithValue("@cat", catBreakdown.ToString());
        upsertCmd.Parameters.AddWithValue("@apps", topApps.ToString());
        upsertCmd.ExecuteNonQuery();

        Logger.Info($"每日汇总已生成：{date}，总活跃 {totalActive} 秒");
    }
}
