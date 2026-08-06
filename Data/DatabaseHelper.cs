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

            CREATE TABLE IF NOT EXISTS DailyTotal (
                Date TEXT NOT NULL PRIMARY KEY,
                TotalActiveSeconds INTEGER NOT NULL DEFAULT 0,
                TotalSeconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS DailyCategorySummary (
                Date TEXT NOT NULL,
                Category TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (Date, Category)
            );

            CREATE TABLE IF NOT EXISTS DailyProcessSummary (
                Date TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '未分类',
                Seconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (Date, ProcessName)
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

            CREATE TABLE IF NOT EXISTS AppColors (
                ProcessName TEXT NOT NULL PRIMARY KEY,
                Color TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE INDEX IF NOT EXISTS IX_Activities_StartTime ON Activities(StartTime);
            CREATE INDEX IF NOT EXISTS IX_Activities_Category ON Activities(Category);
            CREATE INDEX IF NOT EXISTS IX_Activities_ProcessName ON Activities(ProcessName);
            CREATE INDEX IF NOT EXISTS IX_Screenshots_CapturedAt ON Screenshots(CapturedAt);
            CREATE INDEX IF NOT EXISTS IX_DailyCategorySummary_Date ON DailyCategorySummary(Date);
            CREATE INDEX IF NOT EXISTS IX_DailyProcessSummary_Date ON DailyProcessSummary(Date);
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
                    ("开发工具", "#4A90D9", "code", 1),
                    ("社交通讯", "#E67E22", "chat", 2),
                    ("游戏", "#E74C3C", "gamepad", 3),
                    ("办公学习", "#2ECC71", "book", 4),
                    ("浏览器", "#9B59B6", "globe", 5),
                    ("视频娱乐", "#FF6B6B", "video", 6),
                    ("音乐", "#AB47BC", "music", 7),
                    ("设计创作", "#FFA726", "palette", 8),
                    ("实用工具", "#26C6DA", "wrench", 9),
                    ("AI助手", "#EC407A", "robot", 10),
                    ("系统组件", "#7CB9E8", "desktop", 11),
                    ("空闲", "#CFD8DC", "coffee", 12),
                    ("未分类", "#90A4AE", "question", 13),
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
                foreach (var kv in SettingsRepository.Defaults)
                {
                    var insertSet = new SqliteCommand(
                        "INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)", conn);
                    insertSet.Parameters.AddWithValue("@Key", kv.Key);
                    insertSet.Parameters.AddWithValue("@Value", kv.Value);
                    insertSet.ExecuteNonQuery();
                }
            }

            // 插入预置分类规则（如果 Rules 表为空）
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Rules", conn);
            if ((long)countCmd.ExecuteScalar()! == 0)
            {
                // 查分类名→ID 映射
                var catMap = new Dictionary<string, int>();
                using (var catQ = new SqliteCommand("SELECT Id, Name FROM Categories", conn))
                using (var catR = catQ.ExecuteReader())
                    while (catR.Read())
                        catMap[catR.GetString(1)] = catR.GetInt32(0);

                // 预置进程规则从 JSON 文件加载（IsCustom=0 表示预置不可删，全部进程名精确匹配）
                // JSON 文件: Data/seed_rules.json
                var seedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "seed_rules.json");
                var procRuleList = new List<(string proc, string cat)>();
                using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seedPath)))
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var proc = item.GetProperty("process").GetString() ?? "";
                        var cat = item.GetProperty("category").GetString() ?? "";
                        procRuleList.Add((proc, cat));
                    }
                }
                foreach (var (proc, cat) in procRuleList)
                {
                    if (catMap.TryGetValue(cat, out int catId))
                    {
                        using var ins = new SqliteCommand(
                            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@p, NULL, @c, 0)", conn);
                        ins.Parameters.AddWithValue("@p", proc);
                        ins.Parameters.AddWithValue("@c", catId);
                        ins.ExecuteNonQuery();
                    }
                }

                Logger.Info($"预置 {procRuleList.Count} 条分类规则已写入 Rules 表");
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

        string[] tables = { "Activities", "Screenshots", "DailyTotal", "DailyCategorySummary", "DailyProcessSummary", "AISummaries" };
        foreach (var table in tables)
        {
            using var cmd = new SqliteCommand($"DELETE FROM {table}", conn);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 重新分类所有历史活动记录（规则更新后调用）
    /// </summary>
    public static int ReclassifyAll(System.Func<string, string, string> classifyFunc)
    {
        Initialize();
        int updated = 0;
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 取所有非空闲活动记录
        using var selCmd = new SqliteCommand(
            "SELECT Id, ProcessName, WindowTitle FROM Activities WHERE IsIdle=0", conn);
        using var reader = selCmd.ExecuteReader();

        var updates = new List<(long id, string category)>();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string proc = reader.GetString(1);
            string title = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string newCat = classifyFunc(proc, title);
            updates.Add((id, newCat));
        }

        reader.Close();

        foreach (var (id, cat) in updates)
        {
            using var updCmd = new SqliteCommand(
                "UPDATE Activities SET Category=@c WHERE Id=@id", conn);
            updCmd.Parameters.AddWithValue("@c", cat);
            updCmd.Parameters.AddWithValue("@id", id);
            updated += updCmd.ExecuteNonQuery();
        }

        // 重新生成每日汇总
        using var datesCmd = new SqliteCommand(
            "SELECT DISTINCT date(StartTime) FROM Activities", conn);
        using var dateReader = datesCmd.ExecuteReader();
        var dates = new List<string>();
        while (dateReader.Read())
            dates.Add(dateReader.GetString(0));
        dateReader.Close();

        foreach (var date in dates)
            DailySummaryRepository.GenerateForDate(date);

        if (updated > 0)
            Logger.Info($"重新分类完成：更新 {updated} 条活动记录，重新生成 {dates.Count} 天汇总");

        return updated;
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

        // 4. 清每日汇总三张表
        using var cmd5a = new SqliteCommand("DELETE FROM DailyTotal WHERE Date < @DateCutoff", conn);
        cmd5a.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5a.ExecuteNonQuery();
        using var cmd5b = new SqliteCommand("DELETE FROM DailyCategorySummary WHERE Date < @DateCutoff", conn);
        cmd5b.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5b.ExecuteNonQuery();
        using var cmd5c = new SqliteCommand("DELETE FROM DailyProcessSummary WHERE Date < @DateCutoff", conn);
        cmd5c.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5c.ExecuteNonQuery();

        if (totalDeleted > 0)
            Logger.Info($"数据清理：共删除 {totalDeleted} 条旧数据（含活动/截图/AI总结/每日汇总）");

        return totalDeleted;
    }
}
