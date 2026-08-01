using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 数据库帮助类 — SQLite 版，零配置，数据库文件跟着程序走
/// 首次运行自动建库建表插初始数据
/// </summary>
public class DatabaseHelper
{
    // 数据库文件放在程序目录下，方便携带
    private static readonly string DbPath = System.IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");

    private static string ConnectionString => $"Data Source={DbPath}";

    private static bool _initialized = false;

    /// <summary>
    /// 初始化数据库 — 首次运行时自动建表 + 插初始数据
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 建表
        var sql = @"
            CREATE TABLE IF NOT EXISTS Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Color TEXT NOT NULL DEFAULT '#808080',
                Icon TEXT NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Activities (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '未分类',
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                Duration INTEGER NOT NULL DEFAULT 0,
                IsIdle INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS Rules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                TitleKeyword TEXT,
                CategoryId INTEGER NOT NULL,
                IsCustom INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
            );

            CREATE TABLE IF NOT EXISTS Screenshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath TEXT NOT NULL,
                CapturedAt TEXT NOT NULL,
                FileSize INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS DailySummaries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL UNIQUE,
                TotalActiveTime INTEGER NOT NULL DEFAULT 0,
                CategoryBreakdown TEXT,
                TopApps TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS AISummaries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL,
                SummaryText TEXT NOT NULL,
                SummaryType TEXT NOT NULL DEFAULT 'daily',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL UNIQUE,
                Value TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Activities_StartTime ON Activities(StartTime);
            CREATE INDEX IF NOT EXISTS IX_Activities_Category ON Activities(Category);
            CREATE INDEX IF NOT EXISTS IX_Activities_ProcessName ON Activities(ProcessName);
            CREATE INDEX IF NOT EXISTS IX_Screenshots_CapturedAt ON Screenshots(CapturedAt);
            CREATE INDEX IF NOT EXISTS IX_DailySummaries_Date ON DailySummaries(Date);
        ";

        using var cmd = new SqliteCommand(sql, conn);
        cmd.ExecuteNonQuery();

        // 插入预置分类（如果还没有）
        var countCmd = new SqliteCommand("SELECT COUNT(*) FROM Categories", conn);
        if ((long)countCmd.ExecuteScalar()! == 0)
        {
            var cats = new[]
            {
                ("开发", "#4A90D9", "code", 1),
                ("社交", "#E67E22", "chat", 2),
                ("娱乐", "#E74C3C", "gamepad", 3),
                ("学习", "#2ECC71", "book", 4),
                ("系统", "#95A5A6", "desktop", 5),
                ("网页", "#9B59B6", "globe", 6),
                ("空闲", "#BDC3C7", "coffee", 7),
                ("未分类", "#7F8C8D", "question", 8),
            };
            foreach (var (name, color, icon, order) in cats)
            {
                var insertCat = new SqliteCommand(
                    "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, @Icon, @SortOrder)", conn);
                insertCat.Parameters.AddWithValue("@Name", name);
                insertCat.Parameters.AddWithValue("@Color", color);
                insertCat.Parameters.AddWithValue("@Icon", icon);
                insertCat.Parameters.AddWithValue("@SortOrder", order);
                insertCat.ExecuteNonQuery();
            }
        }

        // 插入预置设置项（如果还没有）
        countCmd = new SqliteCommand("SELECT COUNT(*) FROM Settings", conn);
        if ((long)countCmd.ExecuteScalar()! == 0)
        {
            var settings = new[]
            {
                ("PollIntervalSeconds", "3"),
                ("IdleThresholdSeconds", "300"),
                ("AutoStartTracking", "true"),
                ("TrackWindowTitle", "true"),
                ("EnableScreenshot", "false"),
                ("ScreenshotIntervalMinutes", "5"),
                ("ScreenshotPath", ""),
                ("ScreenshotQuality", "medium"),
                ("ColorScheme", "default"),
                ("Use24Hour", "true"),
                ("Theme", "light"),
                ("DataRetentionDays", "90"),
                ("EnableAI", "true"),
                ("AIApiUrl", "https://api.minimax.chat/v1/text/chatcompletion_v2"),
                ("AIApiKey", ""),
                ("AutoDailySummary", "true"),
                ("AutoStartWithWindows", "true"),
                ("MinimizeToTray", "true"),
                ("HotkeyToggleTracking", "Ctrl+Shift+T"),
            };
            foreach (var (key, value) in settings)
            {
                var insertSet = new SqliteCommand(
                    "INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)", conn);
                insertSet.Parameters.AddWithValue("@Key", key);
                insertSet.Parameters.AddWithValue("@Value", value);
                insertSet.ExecuteNonQuery();
            }
        }

        _initialized = true;
    }

    /// <summary>
    /// 测试数据库连接（同时初始化）
    /// </summary>
    public static bool TestConnection()
    {
        try
        {
            Initialize();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 插入一条活动记录
    /// </summary>
    public static long InsertActivity(Models.ActivityRecord activity)
    {
        Initialize();
        const string sql = @"
            INSERT INTO Activities (ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle, CreatedAt)
            VALUES (@ProcessName, @WindowTitle, @Category, @StartTime, @EndTime, @Duration, @IsIdle, @CreatedAt);
            SELECT last_insert_rowid();";

        using var conn = new SqliteConnection(ConnectionString);
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

    /// <summary>
    /// 查询某天的所有活动记录
    /// </summary>
    public static List<Models.ActivityRecord> GetActivitiesByDate(DateTime date)
    {
        Initialize();
        var result = new List<Models.ActivityRecord>();
        string dateStr = date.ToString("yyyy-MM-dd");
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE substr(StartTime, 1, 10) = @DateStr
            ORDER BY StartTime";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Models.ActivityRecord
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

    /// <summary>
    /// 查询某个时间范围内的活动记录
    /// </summary>
    public static List<Models.ActivityRecord> GetActivitiesByRange(DateTime start, DateTime end)
    {
        Initialize();
        var result = new List<Models.ActivityRecord>();
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE StartTime >= @Start AND StartTime < @End
            ORDER BY StartTime";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Models.ActivityRecord
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

    /// <summary>
    /// 获取某天按类别汇总的统计
    /// </summary>
    public static Dictionary<string, int> GetCategorySummaryByDate(DateTime date)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        string dateStr = date.ToString("yyyy-MM-dd");
        const string sql = @"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE substr(StartTime, 1, 10) = @DateStr AND IsIdle = 0
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    /// <summary>
    /// 获取某天按进程汇总的统计
    /// </summary>
    public static Dictionary<string, int> GetProcessSummaryByDate(DateTime date)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        string dateStr = date.ToString("yyyy-MM-dd");
        const string sql = @"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE substr(StartTime, 1, 10) = @DateStr AND IsIdle = 0
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    /// <summary>
    /// 获取某个日期范围内按类别汇总的统计
    /// </summary>
    public static Dictionary<string, int> GetCategorySummaryByRange(DateTime start, DateTime end)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        const string sql = @"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE substr(StartTime, 1, 10) >= @Start AND substr(StartTime, 1, 10) <= @End AND IsIdle = 0
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 获取某个日期范围内按进程汇总的统计
    /// </summary>
    public static Dictionary<string, int> GetProcessSummaryByRange(DateTime start, DateTime end)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        const string sql = @"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE substr(StartTime, 1, 10) >= @Start AND substr(StartTime, 1, 10) <= @End AND IsIdle = 0
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 获取某个日期范围内每天的活跃时长
    /// </summary>
    public static Dictionary<string, int> GetDailyTotalsByRange(DateTime start, DateTime end)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        const string sql = @"
            SELECT substr(StartTime, 1, 10) AS Date, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE substr(StartTime, 1, 10) >= @Start AND substr(StartTime, 1, 10) <= @End AND IsIdle = 0
            GROUP BY Date
            ORDER BY Date";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Start", start.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@End", end.ToString("yyyy-MM-dd"));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// 读取设置项
    /// </summary>
    public static string? GetSetting(string key, string? defaultValue = null)
    {
        Initialize();
        const string sql = "SELECT Value FROM Settings WHERE Key = @Key";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Key", key);

        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? defaultValue : (string)result;
    }

    /// <summary>
    /// 写入/更新设置项
    /// </summary>
    public static void SetSetting(string key, string value)
    {
        Initialize();
        const string sql = @"
            INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = @Value";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Key", key);
        cmd.Parameters.AddWithValue("@Value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 插入截图记录
    /// </summary>
    public static long InsertScreenshot(string filePath, long fileSize)
    {
        Initialize();
        const string sql = @"
            INSERT INTO Screenshots (FilePath, CapturedAt, FileSize, CreatedAt)
            VALUES (@FilePath, @CapturedAt, @FileSize, @CreatedAt);
            SELECT last_insert_rowid();";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@CapturedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 插入 AI 总结
    /// </summary>
    public static void InsertAISummary(DateTime date, string summaryText, string summaryType = "daily")
    {
        Initialize();
        const string sql = @"
            INSERT INTO AISummaries (Date, SummaryText, SummaryType, CreatedAt)
            VALUES (@Date, @SummaryText, @SummaryType, @CreatedAt)";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@SummaryText", summaryText);
        cmd.Parameters.AddWithValue("@SummaryType", summaryType);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取某天的 AI 总结
    /// </summary>
    public static string? GetAISummary(DateTime date)
    {
        Initialize();
        const string sql = "SELECT SummaryText FROM AISummaries WHERE Date = @Date ORDER BY CreatedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));

        var result = cmd.ExecuteScalar();
        return result as string;
    }

    /// <summary>
    /// 清理超过指定天数的旧数据
    /// </summary>
    public static int CleanOldData(int retentionDays)
    {
        Initialize();
        string cutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        const string sql = "DELETE FROM Activities WHERE StartTime < @Cutoff";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Cutoff", cutoff);

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 清空所有数据（保留设置和分类）
    /// </summary>
    public static void ClearAllData()
    {
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        string[] tables = { "Activities", "Screenshots", "DailySummaries", "AISummaries" };
        foreach (var table in tables)
        {
            using var cmd = new SqliteCommand($"DELETE FROM {table}", conn);
            cmd.ExecuteNonQuery();
        }
    }
}
