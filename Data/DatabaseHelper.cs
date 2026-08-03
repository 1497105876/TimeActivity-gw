using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;

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

    public static string ConnectionString => $"Data Source={DbPath}";

    private static bool _initialized = false;

    /// <summary>
    /// 初始化数据库 — 首次运行时自动建表 + 插初始数据
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            // 开启 WAL 模式提升并发性能
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragmaCmd.ExecuteNonQuery();

            Logger.Info("数据库初始化：WAL 已开启");

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

        // 迁移：检查 AISummaries 是否有 AutoType 列，没有就加
        try
        {
            using var checkCol = new SqliteCommand("PRAGMA table_info(AISummaries)", conn);
            using var reader = checkCol.ExecuteReader();
            bool hasAutoType = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "AutoType") { hasAutoType = true; break; }
            }
            if (!hasAutoType)
            {
                using var alterCmd = new SqliteCommand("ALTER TABLE AISummaries ADD COLUMN AutoType TEXT NOT NULL DEFAULT 'manual'", conn);
                alterCmd.ExecuteNonQuery();
                Logger.Info("数据库迁移：AISummaries 表已加 AutoType 字段");
            }
        }
        catch { }

        // 创建唯一索引（如果不存在）— 用于 UPSERT
        try
        {
            using var idxCmd = new SqliteCommand("CREATE UNIQUE INDEX IF NOT EXISTS UX_AISummaries_Type ON AISummaries(Date, SummaryType, AutoType)", conn);
            idxCmd.ExecuteNonQuery();
        }
        catch { }

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
        Logger.Info("数据库初始化完成");
        }
        catch (Exception ex)
        {
            Logger.Error("数据库初始化失败", ex);
            throw;
        }
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
            WHERE date(StartTime) = @DateStr
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
            WHERE date(StartTime) = @DateStr AND IsIdle = 0
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
            WHERE date(StartTime) = @DateStr AND IsIdle = 0
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
        return GetCategorySummaryByRange(start, end, false);
    }

    public static Dictionary<string, int> GetCategorySummaryByRange(DateTime start, DateTime end, bool includeIdle)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        string sql = $@"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) >= @Start AND date(StartTime) <= @End{idleFilter}
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
        return GetProcessSummaryByRange(start, end, false);
    }

    public static Dictionary<string, int> GetProcessSummaryByRange(DateTime start, DateTime end, bool includeIdle)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        string sql = $@"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) >= @Start AND date(StartTime) <= @End{idleFilter}
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
        return GetDailyTotalsByRange(start, end, false);
    }

    public static Dictionary<string, int> GetDailyTotalsByRange(DateTime start, DateTime end, bool includeIdle)
    {
        Initialize();
        var result = new Dictionary<string, int>();
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        string sql = $@"
            SELECT date(StartTime) AS Date, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTime) >= @Start AND date(StartTime) <= @End{idleFilter}
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
    public static void InsertAISummary(DateTime date, string summaryText, string summaryType = "daily", string autoType = "manual")
    {
        Initialize();

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 先删同类型同日期的旧记录，再插入新的
        using var delCmd = new SqliteCommand("DELETE FROM AISummaries WHERE Date=@Date AND SummaryType=@SummaryType AND AutoType=@AutoType", conn);
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

    /// <summary>
    /// 检查是否已有某类型某来源的自动总结
    /// </summary>
    public static bool HasAutoSummary(DateTime date, string summaryType)
    {
        Initialize();
        const string sql = "SELECT COUNT(*) FROM AISummaries WHERE Date=@Date AND SummaryType=@Type AND AutoType='auto'";
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@Type", summaryType);
        long count = (long)cmd.ExecuteScalar()!;
        return count > 0;
    }

    /// <summary>
    /// 获取某天的 AI 总结（默认查 daily 类型）
    /// </summary>
    public static string? GetAISummary(DateTime date, string summaryType = "daily")
    {
        Initialize();
        const string sql = "SELECT SummaryText FROM AISummaries WHERE Date = @Date AND SummaryType = @Type ORDER BY CreatedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(ConnectionString);
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
    public static (string? summary, string? createdAt) GetAISummaryWithMeta(DateTime date, string summaryType, string autoType)
    {
        Initialize();
        const string sql = "SELECT SummaryText, CreatedAt FROM AISummaries WHERE Date = @Date AND SummaryType = @Type AND AutoType = @Auto ORDER BY CreatedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(ConnectionString);
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

    /// <summary>
    /// 清理超过指定天数的旧数据
    /// </summary>
    public static int CleanOldData(int retentionDays)
    {
        Initialize();
        string cutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        string dateCutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd");
        int totalDeleted = 0;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 1. 清 Activities
        using var cmd1 = new SqliteCommand("DELETE FROM Activities WHERE StartTime < @Cutoff", conn);
        cmd1.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd1.ExecuteNonQuery();

        // 2. 清 Screenshots（同时删文件）
        using var cmd2 = new SqliteCommand("SELECT FilePath FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd2.Parameters.AddWithValue("@Cutoff", cutoff);
        using (var reader = cmd2.ExecuteReader())
        {
            while (reader.Read())
            {
                try
                {
                    var p = reader.GetString(0);
                    // 数据库存的可能是相对路径，拼上程序目录
                    string fullPath = System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p);
                    if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
                }
                catch { }
            }
        }
        using var cmd3 = new SqliteCommand("DELETE FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd3.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd3.ExecuteNonQuery();

        // 3. 清 AISummaries（手动总结超期也删）
        using var cmd4 = new SqliteCommand("DELETE FROM AISummaries WHERE Date < @DateCutoff AND AutoType='manual'", conn);
        cmd4.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd4.ExecuteNonQuery();

        // 4. 清 DailySummaries
        using var cmd5 = new SqliteCommand("DELETE FROM DailySummaries WHERE Date < @DateCutoff", conn);
        cmd5.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5.ExecuteNonQuery();

        if (totalDeleted > 0)
            Logger.Info($"数据清理：共删除 {totalDeleted} 条旧数据（含活动/截图/AI总结/每日汇总）");

        return totalDeleted;
    }

    /// <summary>
    /// 生成某天的每日汇总（UPSERT 到 DailySummaries 表）
    /// </summary>
    public static void GenerateDailySummary(string date)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 计算总活跃时长
        using var totalCmd = conn.CreateCommand();
        totalCmd.CommandText = "SELECT COALESCE(SUM(Duration),0) FROM Activities WHERE date(StartTime)=@date AND IsIdle=0";
        totalCmd.Parameters.AddWithValue("@date", date);
        long totalActive = (long)totalCmd.ExecuteScalar();

        // 计算分类细分
        using var catCmd = conn.CreateCommand();
        catCmd.CommandText = @"SELECT Category, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTime)=@date AND IsIdle=0 GROUP BY Category ORDER BY Total DESC";
        catCmd.Parameters.AddWithValue("@date", date);
        var catBreakdown = new System.Text.StringBuilder();
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
        var topApps = new System.Text.StringBuilder();
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

    /// <summary>
    /// 备份数据库到指定路径（VACUUM INTO，不需要停引擎）
    /// </summary>
    public static void BackupTo(string targetPath)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{targetPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
        Logger.Info($"数据库备份到 {targetPath}");
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

    // ========== 分类查询 ==========

    public static List<Models.Category> GetAllCategories()
    {
        Initialize();
        var list = new List<Models.Category>();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT Id, Name, Color, Icon, SortOrder FROM Categories ORDER BY SortOrder, Id", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Models.Category
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Color = reader.GetString(2),
                Icon = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SortOrder = reader.GetInt32(4)
            });
        }
        return list;
    }

    public static void UpdateOrInsertCategory(int id, string name, string color, int sortOrder)
    {
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        if (id > 0)
        {
            using var cmd = new SqliteCommand(
                "UPDATE Categories SET Name=@Name, Color=@Color, SortOrder=@Sort WHERE Id=@Id", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Color", color);
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = new SqliteCommand(
                "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, '', @Sort)", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Color", color);
            cmd.Parameters.AddWithValue("@Sort", sortOrder);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 删除自定义分类（预置 Id 1-8 不可删）
    /// </summary>
    public static bool DeleteCategory(int id)
    {
        if (id <= 8) return false; // 预置分类不可删
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("DELETE FROM Categories WHERE Id=@Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ========== 规则查询 ==========

    public static List<Models.Rule> GetAllRules()
    {
        Initialize();
        var list = new List<Models.Rule>();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT Id, ProcessName, TitleKeyword, CategoryId, IsCustom FROM Rules ORDER BY Id", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Models.Rule
            {
                Id = reader.GetInt32(0),
                ProcessName = reader.GetString(1),
                TitleKeyword = reader.IsDBNull(2) ? null : reader.GetString(2),
                CategoryId = reader.GetInt32(3),
                IsCustom = reader.GetBoolean(4)
            });
        }
        return list;
    }

    public static void InsertRule(string processName, string titleKeyword, int categoryId)
    {
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@P, @T, @C, 1)", conn);
        cmd.Parameters.AddWithValue("@P", processName);
        cmd.Parameters.AddWithValue("@T", string.IsNullOrEmpty(titleKeyword) ? (object)DBNull.Value : titleKeyword);
        cmd.Parameters.AddWithValue("@C", categoryId);
        cmd.ExecuteNonQuery();
    }

    public static void ClearAllRules()
    {
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("DELETE FROM Rules", conn);
        cmd.ExecuteNonQuery();
    }

    // ========== 截图查询 ==========

    /// <summary>
    /// 获取某个时间点最近的一张截图路径（自动拼接相对路径）
    /// </summary>
    public static string? GetScreenshotForTime(DateTime time)
    {
        Initialize();
        const string sql = @"
            SELECT FilePath FROM Screenshots
            WHERE CapturedAt <= @Time
            ORDER BY CapturedAt DESC LIMIT 1";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Time", time.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        var result = cmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
        {
            string path = (string)result;
            if (!System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            if (System.IO.File.Exists(path))
                return path;
        }
        return null;
    }

    // ========== 所有设置查询 ==========

    public static Dictionary<string, string> GetAllSettings()
    {
        Initialize();
        var dict = new Dictionary<string, string>();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand("SELECT Key, Value FROM Settings", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        return dict;
    }
}
