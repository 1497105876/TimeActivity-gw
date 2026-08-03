using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;

namespace TimeActivity.Data;

/// <summary>
/// 数据库基础设施 — 负责建库建表、连接管理、备份、数据清理
/// 各表的 CRUD 操作请用对应的 Repository 类
/// </summary>
public class DatabaseHelper
{
    private static readonly string DbPath = Path.Combine(
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

            using var createCmd = new SqliteCommand(sql, conn);
            createCmd.ExecuteNonQuery();

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
                    string fullPath = Path.IsPathRooted(p) ? p : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }
                catch { }
            }
        }
        using var cmd3 = new SqliteCommand("DELETE FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd3.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd3.ExecuteNonQuery();

        // 3. 清 AISummaries（手动总结超期也删，自动总结永久保留）
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
}
