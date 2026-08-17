using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;
using TimeActivity.Helpers;

namespace TimeActivity.Data;

/// <summary>
/// 数据库基础设施 — 负责建库建表、连接管理、初始化。
/// 各表的 CRUD 操作请用对应的 Repository 类；
/// 备份 / 清空 / 重新分类 / 清理等运维操作见 DatabaseHelper.Maintenance.cs（同属一个 static partial class）。
/// </summary>
public static partial class DatabaseHelper
{
    // 数据库文件路径，放在程序目录下
    private static readonly string DbPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");

    // SQLite 连接字符串，直接指向数据库文件
    public static string ConnectionString => $"Data Source={DbPath}";

    // 防止重复初始化的标记（配合 _initLock 保证并发首调安全）
    private static bool _initialized = false;
    private static readonly object _initLock = new();

    /// <summary>
    /// 初始化数据库 — 首次运行时自动建表 + 插初始数据
    /// </summary>
    public static void Initialize()
    {
        // 双重检查锁：避免并发首调时重复建表 / 重复插入预置数据
        if (_initialized) return;
        lock (_initLock)
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
            catch (Exception ex)
            {
                // 列迁移是 AI 总结写入的前提，失败不应静默吞掉，
                // 抛出让外层 Initialize 统一记日志并报错，避免后续 Insert 因缺列而隐蔽失败。
                Logger.Error("AISummaries AutoType 列迁移失败", ex);
                throw;
            }
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
                // 预置分类直接复用 CategoryRepository 的权威定义，避免重复硬编码导致不同步
                foreach (var (name, color, icon, order) in CategoryRepository.PresetCategories)
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
}
