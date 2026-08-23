// ============================================================================
// ActivityRepository.cs — Activities 活动明细表的仓储（静态类）
// 职责：活动记录插入/按日查询/区间聚合（分类、进程、每日总量）；
//       GetUsedProcessNames 供设置页规则管理展示"用过的应用"。
// 查询均以 StartTime 的 date()/区间为过滤条件，依赖三个索引加速。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TimeActivity.Models;
using TimeActivity.Services;
using TimeActivity.Helpers;

namespace TimeActivity.Data;

/// <summary>
/// 活动记录仓储 — 负责 Activities 表的增删查
/// </summary>
public static class ActivityRepository
{
    // 确保数据库已初始化（首次调用时触发建表）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 插入一条活动记录，返回新记录的自增 Id
    /// </summary>
    /// <param name="activity">要插入的活动记录（进程名、窗口标题、分类、起止时间、时长、是否空闲）</param>
    /// <returns>新插入记录的自增 Id</returns>
    public static long Insert(ActivityRecord activity)
    {
        EnsureInit();
        // 插入活动记录并取回自增 Id
        // 2026-08-23 UTC 双列：本地时间列照旧（展示用），同时写入 UTC 列（统计口径抗时区/改钟）
        const string sql = @"
            INSERT INTO Activities (ProcessName, WindowTitle, Category, StartTime, EndTime, StartTimeUtc, EndTimeUtc, Duration, IsIdle, CreatedAt)
            VALUES (@ProcessName, @WindowTitle, @Category, @StartTime, @EndTime, @StartTimeUtc, @EndTimeUtc, @Duration, @IsIdle, @CreatedAt);
            SELECT last_insert_rowid();";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProcessName", activity.ProcessName);
        cmd.Parameters.AddWithValue("@WindowTitle", activity.WindowTitle ?? "");
        cmd.Parameters.AddWithValue("@Category", activity.Category);
        // 时间统一用 yyyy-MM-dd HH:mm:ss.fff 格式存储，精确到毫秒
        cmd.Parameters.AddWithValue("@StartTime", activity.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@EndTime", activity.EndTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        // UTC 列：ISO8601 带 Z 后缀，由本地值换算而来
        cmd.Parameters.AddWithValue("@StartTimeUtc", activity.StartTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        cmd.Parameters.AddWithValue("@EndTimeUtc", activity.EndTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        cmd.Parameters.AddWithValue("@Duration", activity.Duration);
        // IsIdle 存为 0/1，SQLite 没有布尔类型
        cmd.Parameters.AddWithValue("@IsIdle", activity.IsIdle ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 查询某天所有活动记录，按开始时间排序
    /// </summary>
    /// <param name="date">要查询的日期（只看日期部分，不看时间）</param>
    /// <returns>该天所有活动记录列表，按 StartTime 升序</returns>
    public static List<ActivityRecord> GetByDate(DateTime date)
    {
        EnsureInit();
        var result = new List<ActivityRecord>();
        string dateStr = date.ToDateKey();
        // 用 date(StartTime) 提取日期部分做比较，省去时间部分的干扰
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') = @DateStr
            ORDER BY StartTime";

        using var conn = DbAccess.Open();
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
                StartTime = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                EndTime = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Duration = reader.GetInt32(6),
                IsIdle = reader.GetInt32(7) == 1
            });
        }

        return result;
    }

    /// <summary>
    /// 查询某个时间范围内的活动记录（左闭右开）
    /// </summary>
    /// <param name="start">起始时间（包含）</param>
    /// <param name="end">结束时间（不包含）</param>
    /// <returns>范围内的活动记录列表，按 StartTime 升序</returns>
    public static List<ActivityRecord> GetByRange(DateTime start, DateTime end)
    {
        EnsureInit();
        var result = new List<ActivityRecord>();
        // StartTime >= start AND StartTime < end，左闭右开区间
        // 2026-08-23：改用 UTC 列做范围比较（入参为本地时间，内部换算），抗时区/改钟
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE StartTimeUtc >= @Start AND StartTimeUtc < @End
            ORDER BY StartTimeUtc";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        cmd.Parameters.AddWithValue("@End", end.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityRecord
            {
                Id = reader.GetInt64(0),
                ProcessName = reader.GetString(1),
                WindowTitle = reader.GetString(2),
                Category = reader.GetString(3),
                StartTime = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                EndTime = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Duration = reader.GetInt32(6),
                IsIdle = reader.GetInt32(7) == 1
            });
        }

        return result;
    }

    /// <summary>
    /// 按分类汇总某天活动时长（排除空闲时间）
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <returns>字典：分类名 → 总秒数，按时长降序排列</returns>
    public static Dictionary<string, int> GetCategorySummaryByDate(DateTime date)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string dateStr = date.ToDateKey();
        // 按分类汇总时长，排除空闲记录，按总时长降序排
        const string sql = @"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') = @DateStr AND IsIdle = 0
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 按进程名汇总某天活动时长（排除空闲时间）
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <returns>字典：进程名 → 总秒数，按时长降序排列</returns>
    public static Dictionary<string, int> GetProcessSummaryByDate(DateTime date)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string dateStr = date.ToDateKey();
        // 按进程名汇总时长，排除空闲记录，按总时长降序排
        const string sql = @"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') = @DateStr AND IsIdle = 0
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 按分类汇总日期范围内的时长，默认排除空闲
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：分类名 → 总秒数，按时长降序</returns>
    public static Dictionary<string, int> GetCategorySummaryByRange(DateTime start, DateTime end)
        => GetCategorySummaryByRange(start, end, false);

    /// <summary>
    /// 按分类汇总日期范围内的时长，可选择是否包含空闲时间
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <param name="includeIdle">是否包含空闲记录</param>
    /// <returns>字典：分类名 → 总秒数，按时长降序</returns>
    public static Dictionary<string, int> GetCategorySummaryByRange(DateTime start, DateTime end, bool includeIdle)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        // includeIdle=false 时追加 IsIdle=0 过滤条件
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        // 用 date(StartTime) 取日期部分做范围比较
        string sql = $@"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') >= @Start AND date(StartTimeUtc,'localtime') <= @End{idleFilter}
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 按进程名汇总日期范围内的时长，默认排除空闲
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：进程名 → 总秒数，按时长降序</returns>
    public static Dictionary<string, int> GetProcessSummaryByRange(DateTime start, DateTime end)
        => GetProcessSummaryByRange(start, end, false);

    /// <summary>
    /// 按进程名汇总日期范围内的时长，可选择是否包含空闲时间
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <param name="includeIdle">是否包含空闲记录</param>
    /// <returns>字典：进程名 → 总秒数，按时长降序</returns>
    public static Dictionary<string, int> GetProcessSummaryByRange(DateTime start, DateTime end, bool includeIdle)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        // 按进程名汇总，GROUP BY ProcessName，按总时长降序
        string sql = $@"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') >= @Start AND date(StartTimeUtc,'localtime') <= @End{idleFilter}
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 获取日期范围内每天的活跃时长合计，默认排除空闲
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：日期字符串(yyyy-MM-dd) → 总秒数，按日期升序</returns>
    public static Dictionary<string, int> GetDailyTotalsByRange(DateTime start, DateTime end)
        => GetDailyTotalsByRange(start, end, false);

    /// <summary>
    /// 获取日期范围内每天的活跃时长合计，可选择是否包含空闲时间
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <param name="includeIdle">是否包含空闲记录</param>
    /// <returns>字典：日期字符串(yyyy-MM-dd) → 总秒数，按日期升序</returns>
    public static Dictionary<string, int> GetDailyTotalsByRange(DateTime start, DateTime end, bool includeIdle)
    {
        EnsureInit();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        // 按日期分组汇总，GROUP BY date(StartTime)，用于趋势图展示
        string sql = $@"
            SELECT date(StartTimeUtc,'localtime') AS Date, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') >= @Start AND date(StartTimeUtc,'localtime') <= @End{idleFilter}
            GROUP BY Date
            ORDER BY Date";

        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 获取所有用户实际使用过的进程名（去重）
    /// </summary>
    public static HashSet<string> GetUsedProcessNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 统一走 DbAccess.Open()，它会先确保数据库已初始化（建表），
        // 避免本方法作为首个查询时 Activities 表尚不存在而抛 no such table
        using var conn = DbAccess.Open();
        // 排除空闲记录和占位符 "(空闲)"
        using var cmd = new SqliteCommand(
            "SELECT DISTINCT ProcessName FROM Activities WHERE IsIdle = 0", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            // 过滤掉空进程名和 "(空闲)" 占位符
            if (!string.IsNullOrEmpty(name) && name != "(空闲)")
                result.Add(name);
        }
        return result;
    }
}
