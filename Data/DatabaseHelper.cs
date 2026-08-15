using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;
using TimeActivity.Helpers;

namespace TimeActivity.Data;

/// <summary>
/// 数据库基础设施 — 负责建库建表、连接管理、备份、数据清理
/// 各表的 CRUD 操作请用对应的 Repository 类
/// </summary>
public class DatabaseHelper
{
    // 数据库文件路径，放在程序目录下
    private static readonly string DbPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");

    // SQLite 连接字符串，直接指向数据库文件
    public static string ConnectionString => $"Data Source={DbPath}";

    // 防止重复初始化的标记
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

            // 开启 WAL 模式提升并发读写性能，NORMAL 同步级别兼顾安全和速度
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragmaCmd.ExecuteNonQuery();

            Logger.Info("数据库初始化：WAL 已开启");

            // 建表 SQL——一次性创建所有表和索引，IF NOT EXISTS 保证重复执行不报错
            var sql = @"
            CREATE TABLE IF NOT EXISTS Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Color TEXT NOT NULL DEFAULT '#808080',
                Icon TEXT NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            -- 活动记录表：每条记录代表一个时间段的使用
            CREATE TABLE IF NOT EXISTS Activities (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '未分类',
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                Duration INTEGER NOT NULL DEFAULT 0,          -- 时长，单位秒
                IsIdle INTEGER NOT NULL DEFAULT 0,            -- 0=活跃，1=空闲
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            -- 分类规则表：进程名/窗口标题关键词 → 分类的映射
            CREATE TABLE IF NOT EXISTS Rules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                TitleKeyword TEXT,                            -- 可为 NULL，表示只按进程名匹配
                CategoryId INTEGER NOT NULL,
                IsCustom INTEGER NOT NULL DEFAULT 0,          -- 0=预置不可删，1=用户自定义
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
            );

            -- 截图记录表
            CREATE TABLE IF NOT EXISTS Screenshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath TEXT NOT NULL,
                CapturedAt TEXT NOT NULL,
                FileSize INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            -- 每日总时长汇总表（预聚合，加速查询）
            CREATE TABLE IF NOT EXISTS DailyTotal (
                Date TEXT NOT NULL PRIMARY KEY,
                TotalActiveSeconds INTEGER NOT NULL DEFAULT 0,  -- 活跃时长（排除空闲）
                TotalSeconds INTEGER NOT NULL DEFAULT 0,         -- 总时长（含空闲）
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            -- 每日分类汇总表（预聚合）
            CREATE TABLE IF NOT EXISTS DailyCategorySummary (
                Date TEXT NOT NULL,
                Category TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (Date, Category)
            );

            -- 每日进程汇总表（预聚合）
            CREATE TABLE IF NOT EXISTS DailyProcessSummary (
                Date TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '未分类',
                Seconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (Date, ProcessName)
            );

            -- AI 总结记录表
            CREATE TABLE IF NOT EXISTS AISummaries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL,
                SummaryText TEXT NOT NULL,
                SummaryType TEXT NOT NULL DEFAULT 'daily',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            -- 设置项表（键值对存储）
            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL UNIQUE,
                Value TEXT
            );

            -- 应用单独颜色表（独立于分类颜色，给特定应用自定义颜色）
            CREATE TABLE IF NOT EXISTS AppColors (
                ProcessName TEXT NOT NULL PRIMARY KEY,
                Color TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            -- 索引：加速常用查询（按时间、分类、进程名查活动记录）
            CREATE INDEX IF NOT EXISTS IX_Activities_StartTime ON Activities(StartTime);
            CREATE INDEX IF NOT EXISTS IX_Activities_Category ON Activities(Category);
            CREATE INDEX IF NOT EXISTS IX_Activities_ProcessName ON Activities(ProcessName);
            CREATE INDEX IF NOT EXISTS IX_Screenshots_CapturedAt ON Screenshots(CapturedAt);
            CREATE INDEX IF NOT EXISTS IX_DailyCategorySummary_Date ON DailyCategorySummary(Date);
            CREATE INDEX IF NOT EXISTS IX_DailyProcessSummary_Date ON DailyProcessSummary(Date);
        ";

            using var createCmd = new SqliteCommand(sql, conn);
            createCmd.ExecuteNonQuery();

            // 迁移：检查 AISummaries 表是否有 AutoType 列，没有就加（老版本数据库升级用）
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
            catch (Exception ex) { Logger.Error("AISummaries AutoType 列迁移失败", ex); }
            // 创建唯一索引：同一天同一类型同一来源只能有一条总结
            try
            {
                using var idxCmd = new SqliteCommand("CREATE UNIQUE INDEX IF NOT EXISTS UX_AISummaries_Type ON AISummaries(Date, SummaryType, AutoType)", conn);
                idxCmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Logger.Error("AISummaries 唯一索引创建失败", ex); }
            // Categories 表为空时插入预置分类（首次运行）
            var countCmd = new SqliteCommand("SELECT COUNT(*) FROM Categories", conn);
            if ((long)(countCmd.ExecuteScalar() ?? 0L) == 0)
            {
                // 预置分类列表：名称、颜色、图标标识、排序序号
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

            // Settings 表为空时插入预置设置项（首次运行）
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Settings", conn);
            if ((long)(countCmd.ExecuteScalar() ?? 0L) == 0)
            {
                // 从 SettingsRepository.Defaults 读取默认设置写入数据库
                foreach (var kv in SettingsRepository.Defaults)
                {
                    var insertSet = new SqliteCommand(
                        "INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)", conn);
                    insertSet.Parameters.AddWithValue("@Key", kv.Key);
                    insertSet.Parameters.AddWithValue("@Value", kv.Value);
                    insertSet.ExecuteNonQuery();
                }
            }

            // Rules 表为空时插入预置分类规则（首次运行）
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Rules", conn);
            if ((long)(countCmd.ExecuteScalar() ?? 0L) == 0)
            {
                // 先查分类名 → Id 映射，后面插入规则要用
                var catMap = new Dictionary<string, int>();
                using (var catQ = new SqliteCommand("SELECT Id, Name FROM Categories", conn))
                using (var catR = catQ.ExecuteReader())
                    while (catR.Read())
                        catMap[catR.GetString(1)] = catR.GetInt32(0);

                // 预置进程规则从 JSON 文件加载（IsCustom=0 表示预置不可删，全部进程名精确匹配）
                // JSON 文件: Data/seed_rules.json
                var seedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "seed_rules.json");
                var procRuleList = new List<(string proc, string cat)>();
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seedPath)))
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var proc = item.GetProperty("process").GetString() ?? "";
                            var cat = item.GetProperty("category").GetString() ?? "";
                            procRuleList.Add((proc, cat));
                        }
                    }
                }
                catch (Exception seedEx)
                {
                    Logger.Error($"预置规则 JSON 加载失败（{seedPath}），跳过预置规则。用户可在设置中手动添加规则。", seedEx);
                }
                // 逐条插入预置规则，进程名匹配分类名对应的 Id
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
    /// 测试数据库连接是否正常（顺带触发初始化）
    /// </summary>
    /// <returns>连接成功返回 true，否则 false</returns>
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
    /// 备份数据库到指定路径（使用 VACUUM INTO，不需要停引擎，在线备份）
    /// </summary>
    /// <param name="targetPath">备份文件保存路径</param>
    public static void BackupTo(string targetPath)
    {
        // VACUUM INTO 相当于在线导出一个干净的副本，不需要停数据库
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{targetPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
        Logger.Info($"数据库备份到 {targetPath}");
    }

    /// <summary>
    /// 清空所有用户数据（活动记录、截图、AI总结、每日汇总），保留设置和分类
    /// </summary>
    public static void ClearAllData()
    {
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 用事务包住所有 DELETE，中途失败不会丢部分数据
        using var transaction = conn.BeginTransaction();
        try
        {
            // 按表逐个清空，不删 Categories/Settings/AppColors/Rules
            string[] tables = { "Activities", "Screenshots", "DailyTotal", "DailyCategorySummary", "DailyProcessSummary", "AISummaries" };
            foreach (var table in tables)
            {
                using var cmd = new SqliteCommand($"DELETE FROM {table}", conn, transaction);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 重新分类所有历史活动记录（规则更新后调用）
    /// </summary>
    /// <param name="classifyFunc">分类函数：传入进程名和窗口标题，返回分类名</param>
    /// <returns>更新的记录数</returns>
    public static int ReclassifyAll(System.Func<string, string, string> classifyFunc)
    {
        Initialize();
        int updated = 0;
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 用事务包裹整个操作，失败时回滚保证数据一致性
        using var transaction = conn.BeginTransaction();

        try
        {
            // 取所有非空闲活动记录，读到内存里再批量更新（避免 reader 打开时执行命令）
            using var selCmd = new SqliteCommand(
                "SELECT Id, ProcessName, WindowTitle FROM Activities WHERE IsIdle=0", conn, transaction);
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
                    "UPDATE Activities SET Category=@c WHERE Id=@id", conn, transaction);
                updCmd.Parameters.AddWithValue("@c", cat);
                updCmd.Parameters.AddWithValue("@id", id);
                updated += updCmd.ExecuteNonQuery();
            }

            // 重新生成每日汇总（在同一个事务内）
            using var datesCmd = new SqliteCommand(
                "SELECT DISTINCT date(StartTime) FROM Activities", conn, transaction);
            using var dateReader = datesCmd.ExecuteReader();
            var dates = new List<string>();
            while (dateReader.Read())
                dates.Add(dateReader.GetString(0));
            dateReader.Close();

            // 逐天重新生成汇总，全部在事务内完成
            foreach (var date in dates)
                DailySummaryRepository.GenerateForDate(date, conn, transaction);

            transaction.Commit();

            if (updated > 0)
                Logger.Info($"重新分类完成：更新 {updated} 条活动记录，重新生成 {dates.Count} 天汇总");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Logger.Error("重新分类失败，已回滚", ex);
            throw;
        }

        return updated;
    }

    /// <summary>
    /// 清理超过指定天数的旧数据（活动记录、截图、AI总结、每日汇总）
    /// </summary>
    /// <param name="retentionDays">数据保留天数，超过此天数的将被删除</param>
    /// <returns>总共删除的记录数</returns>
    public static int CleanOldData(int retentionDays)
    {
        Initialize();
        // 计算截止时间：超过这个时间的数据将被清理
        string cutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        string dateCutoff = DateTime.Now.AddDays(-retentionDays).ToDateKey();
        int totalDeleted = 0;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 1. 清 Activities——删除过期的活动记录
        using var cmd1 = new SqliteCommand("DELETE FROM Activities WHERE StartTime < @Cutoff", conn);
        cmd1.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd1.ExecuteNonQuery();

        // 2. 清 Screenshots——先查出文件路径删物理文件，再删数据库记录
        using var cmd2 = new SqliteCommand("SELECT FilePath FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd2.Parameters.AddWithValue("@Cutoff", cutoff);
        using (var reader = cmd2.ExecuteReader())
        {
            while (reader.Read())
            {
                try
                {
                    // 相对路径拼接程序目录，绝对路径直接用
                    var p = reader.GetString(0);
                    string fullPath = Path.IsPathRooted(p) ? p : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }
                catch (Exception ex) { Logger.Error($"删除旧截图文件失败", ex); }
            }
        }
        using var cmd3 = new SqliteCommand("DELETE FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd3.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd3.ExecuteNonQuery();

        // 3. 清 AISummaries——手动总结超期删除，自动总结永久保留
        using var cmd4 = new SqliteCommand("DELETE FROM AISummaries WHERE Date < @DateCutoff AND AutoType='manual'", conn);
        cmd4.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd4.ExecuteNonQuery();

        // 4. 清每日汇总三张表——按日期删除过期数据
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
