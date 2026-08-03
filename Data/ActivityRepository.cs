using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TimeActivity.Models;
using TimeActivity.Services;

namespace TimeActivity.Data;

/// <summary>
/// 活动记录仓储 — 负责 Activities 表的增删查
/// </summary>
public static class ActivityRepository
{
    private static void EnsureInit() => DatabaseHelper.Initialize();

    public static long Insert(ActivityRecord activity)
    {
        EnsureInit();
        const string sql = @"
            INSERT INTO Activities (ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle, CreatedAt)
            VALUES (@ProcessName, @WindowTitle, @Category, @StartTime, @EndTime, @Duration, @IsIdle, @CreatedAt);
            SELECT last_insert_rowid();";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProcessName", activity.ProcessName);
        cmd.Parameters.AddWithValue("@WindowTitle", activity.WindowTitle ?? "");
        cmd.Parameters.AddWithValue("@Category", activity.Category);
        cmd.Parameters.AddWithValue("@StartTime", activity.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@EndTime", activity.EndTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@Duration", activity.Duration);
        cmd.Parameters.AddWithValue("@IsIdle", activity.IsIdle ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return (long)cmd.ExecuteScalar()!;
    }

    public static List<ActivityRecord> GetByDate(DateTime date)
    {
        EnsureInit();
        var result = new List<ActivityRecord>();
        string dateStr = date.ToString("yyyy-MM-dd");
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE date(StartTime) = @DateStr
            ORDER BY StartTime";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityRecord
            {
                Id = reader.GetInt64(0),
                ProcessName = reader.GetString(1),
                WindowTitle = reader.GetString(2),
                Category = reader.GetString(3),
                StartTime = DateTime.Parse(reader.GetString(4)),
                EndTime = DateTime.Parse(reader.GetString(5)),
                Duration = reader.GetInt32(6),
                IsIdle = reader.GetInt32(7) == 1
            });
        }

        return result;
    }

    public static List<ActivityRecord> GetByRange(DateTime start, DateTime end)
    {
        EnsureInit();
        var result = new List<ActivityRecord>();
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE StartTime >= @Start AND StartTime < @End
            ORDER BY StartTime";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityRecord
            {
                Id = reader.GetInt64(0),
                ProcessName = reader.GetString(1),
                WindowTitle = reader.GetString(2),
                Category = reader.GetString(3),
                StartTime = DateTime.Parse(reader.GetString(4)),
                EndTime = DateTime.Parse(reader.GetString(5)),
                Duration = reader.GetInt32(6),
                IsIdle = reader.GetInt32(7) == 1
            });
        }

        return result;
    }

    public static Dictionary<string, int> GetCategorySummaryByDate(DateTime date)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string dateStr = date.ToString("yyyy-MM-dd");
        const string sql = @"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) = @DateStr AND IsIdle = 0
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public static Dictionary<string, int> GetProcessSummaryByDate(DateTime date)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string dateStr = date.ToString("yyyy-MM-dd");
        const string sql = @"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) = @DateStr AND IsIdle = 0
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public static Dictionary<string, int> GetCategorySummaryByRange(DateTime start, DateTime end)
        => GetCategorySummaryByRange(start, end, false);

    public static Dictionary<string, int> GetCategorySummaryByRange(DateTime start, DateTime end, bool includeIdle)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        string sql = $@"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) >= @Start AND date(StartTime) <= @End{idleFilter}
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public static Dictionary<string, int> GetProcessSummaryByRange(DateTime start, DateTime end)
        => GetProcessSummaryByRange(start, end, false);

    public static Dictionary<string, int> GetProcessSummaryByRange(DateTime start, DateTime end, bool includeIdle)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        string sql = $@"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) >= @Start AND date(StartTime) <= @End{idleFilter}
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public static Dictionary<string, int> GetDailyTotalsByRange(DateTime start, DateTime end)
        => GetDailyTotalsByRange(start, end, false);

    public static Dictionary<string, int> GetDailyTotalsByRange(DateTime start, DateTime end, bool includeIdle)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        string sql = $@"
            SELECT date(StartTime) AS Date, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) >= @Start AND date(StartTime) <= @End{idleFilter}
            GROUP BY Date
            ORDER BY Date";

        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }
}
