// ============================================================================
// DatabaseHelper.cs — SQLite 数据库基础设施（建库/建表/迁移/种子数据）
// 职责：
//   1) Initialize()：幂等初始化 —— 建全部表与索引、WAL 参数、
//      AISummaries.AutoType 列迁移、预置分类/设置/规则种子数据；
//   2) 连接字符串与库文件路径的唯一权威定义；
//   3) TestConnection()：连通性自检。
// 运维操作（备份/清空/重分类/清理）在 partial 文件 DatabaseHelper.Maintenance.cs。
// 表结构总览：
//   Categories 分类 | Activities 活动明细 | Rules 分类规则 | Screenshots 截图索引
//   DailyTotal / DailyCategorySummary / DailyProcessSummary 每日预聚合
//   AISummaries AI 总结 | Settings 键值设置 | AppColors 应用专属颜色
// ============================================================================
// 基础类型（AppDomain、Exception、Dictionary）
using System;
// 文件路径操作（Path/File）
using System.IO;
// SQLite ADO.NET 提供程序（SqliteConnection/SqliteCommand 等）
using Microsoft.Data.Sqlite;
// 日志服务（Logger.Info/Error）
using TimeActivity.Services;
// 帮助扩展（ToDateKey 等）
using TimeActivity.Helpers;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 数据库基础设施 — 负责建库建表、连接管理、初始化。
/// 各表的 CRUD 操作请用对应的 Repository 类；
/// 备份 / 清空 / 重新分类 / 清理等运维操作见 DatabaseHelper.Maintenance.cs（同属一个 static partial class）。
/// </summary>
public static partial class DatabaseHelper
{
    // 数据库文件路径，放在程序目录下
    // BaseDirectory=exe 所在目录，便携式设计（库文件随程序目录走）
    // 库文件名固定为 timeactivity.db；开启 WAL 后同目录还会出现 timeactivity.db-wal 与
    // timeactivity.db-shm 两个附属文件，手工拷贝/备份时要把三个一起带走，否则可能丢最后一批提交
    private static readonly string DbPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");

    // SQLite 连接字符串，直接指向数据库文件
    // 全项目唯一权威定义：各仓储一律引用本属性，禁止自行拼接
    // 未指定 Password / Mode / Cache 等参数，全部走 Microsoft.Data.Sqlite 的默认（可读可写、库不存在则建）
    public static string ConnectionString => $"Data Source={DbPath}";

    // 防止重复初始化的标记（配合 _initLock 保证并发首调安全）
    // 非 volatile：正确性由下方 lock 的进入/退出屏障保证
    // 语义：只有初始化完整跑完才会置 true；中途抛异常则保持 false，下次调用可以重新尝试
    private static bool _initialized = false;
    // 初始化专用锁对象：并发首次调用时串行化整个初始化流程
    // 只保护"初始化"这一段，不保护后续业务读写——业务层的并发由 SQLite 自身锁 + WAL 处理
    private static readonly object _initLock = new();

    /// <summary>
    /// 初始化数据库 — 首次运行时自动建表 + 插初始数据
    /// 幂等：重复调用无副作用；并发安全：双重检查锁。
    /// </summary>
    public static void Initialize()
    {
        // 双重检查锁：避免并发首调时重复建表 / 重复插入预置数据
        if (_initialized) return;      // 快路径：已初始化直接返回
        lock (_initLock)               // 慢路径：加锁后再次确认
        {
            if (_initialized) return;
            // 二次确认通过，开始执行初始化（全程持有 _initLock）
            // 整个初始化流程包在 try 中：任何一步失败都记日志并向上抛出
            // 失败时 _initialized 仍为 false，下次调用会整体重跑一遍——所以各步骤必须写成幂等的
            try
            {
            // 连接对象由 using 托管，异常路径同样能释放
            using var conn = new SqliteConnection(ConnectionString); // 创建连接（打开时才真正建立文件）
            // 真正打开连接：首次 Open 时若库文件不存在会自动创建空库
            // 如果程序目录没有写权限（例如装在 Program Files 又没提权），失败点就在这一行
            conn.Open();

            // 开启 WAL 模式提升并发读写性能，NORMAL 同步级别兼顾安全和速度
            // WAL：写入先进 -wal 日志，读不阻塞写，适合采集器高频写入场景
            // 注意作用域：journal_mode=WAL 是"库级"持久属性，设一次就一直生效；
            //             synchronous 是"连接级"的，每个新建的连接都会回到默认值，
            //             也就是说这里只是让初始化这条连接跑得快点，真正高频写入的连接靠 WAL 本身受益
            using var pragmaCmd = conn.CreateCommand();
            // 两条 PRAGMA 合并为一条命令执行；journal_mode=WAL 是持久化属性
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            // 执行设置
            pragmaCmd.ExecuteNonQuery();

            Logger.Info("数据库初始化：WAL 已开启");

            // 建表 SQL——一次性创建所有表和索引，IF NOT EXISTS 保证重复执行不报错
            // 建表口径：这段 DDL 只对"全新的空库"生效；老库要加列只能走下面的 ALTER 迁移，
            //           所以后续给 Activities / AISummaries 补列都是在建表之后再 ALTER，而不是改这里。
            // 表与字段速查（时间列一律是 TEXT，SQLite 没有原生日期类型，靠等宽格式保证字符串比较正确）：
            //   Categories           分类字典   Id / Name / Color / Icon / SortOrder
            //   Activities           活动明细   Id / ProcessName / WindowTitle / Category /
            //                                  StartTime / EndTime / Duration / IsIdle / CreatedAt
            //                                  （StartTimeUtc / EndTimeUtc 为迁移补的列，见下方）
            //   Rules                分类规则   Id / ProcessName / TitleKeyword / CategoryId / IsCustom
            //   Screenshots          截图索引   Id / FilePath / CapturedAt / FileSize / CreatedAt
            //   DailyTotal           每日总量   Date(PK) / TotalActiveSeconds / TotalSeconds / CreatedAt
            //   DailyCategorySummary 每日分类   Date + Category(复合PK) / Seconds / CreatedAt
            //   DailyProcessSummary  每日进程   Date + ProcessName(复合PK) / Category / Seconds / CreatedAt
            //   AISummaries          AI 总结    Id / Date / SummaryText / SummaryType / CreatedAt
            //                                  （AutoType 为迁移补的列，见下方）
            //   Settings             键值设置   Id / Key(UNIQUE) / Value
            //   AppColors            应用颜色   ProcessName(PK) / Color / UpdatedAt
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

            // 单条命令执行整段 DDL 脚本（SQLite 支持一次执行多条语句）
            using var createCmd = new SqliteCommand(sql, conn);
            createCmd.ExecuteNonQuery(); // 执行建表脚本（幂等）

            // 迁移：检查 AISummaries 表是否有 AutoType 列，没有就加（老版本数据库升级用）
            try
            {
                using var checkCol = new SqliteCommand("PRAGMA table_info(AISummaries)", conn); // 读取表结构
                using var reader = checkCol.ExecuteReader();
                // 标记：表内是否已存在 AutoType 列
                bool hasAutoType = false;
                while (reader.Read()) // 逐列检查是否已存在 AutoType
                {
                    if (reader.GetString(1) == "AutoType") { hasAutoType = true; break; } // 第1列是列名
                }
                if (!hasAutoType) // 缺列则补齐（区分 manual/auto 总结的依据）
                {
                    // 默认值 'manual' 的用意：老库里已有的总结都是用户手动触发生成的，
                    // 迁移后它们被归为 manual，从而不会被 InvalidateRecent 的清理逻辑删掉；
                    // NOT NULL 在这里可行是因为带了 DEFAULT（SQLite 的 ALTER ADD COLUMN 限制）
                    using var alterCmd = new SqliteCommand("ALTER TABLE AISummaries ADD COLUMN AutoType TEXT NOT NULL DEFAULT 'manual'", conn);
                    // 执行加列
                    alterCmd.ExecuteNonQuery();
                    // 记一条迁移日志，便于在日志里确认老库升级路径
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
            // 为什么放在建表脚本外面单独建：AutoType 列不在 CREATE TABLE 里，是靠上面的迁移 ALTER 出来的，
            // 索引要引用的列必须先存在，所以这段只能排在迁移之后（新库老库都走同一条路径）
            try
            {
                using var idxCmd = new SqliteCommand("CREATE UNIQUE INDEX IF NOT EXISTS UX_AISummaries_Type ON AISummaries(Date, SummaryType, AutoType)", conn);
                // 老库若已有脏重复数据，建唯一索引会失败——仅记日志，不阻断启动
                // 后果：约束没建起来，唯一性只剩 AISummaryRepository.Insert 里"先删后插"那道逻辑保障
                idxCmd.ExecuteNonQuery();
            }
            // 吞掉异常是刻意的：历史脏数据属于可容忍问题，不该让程序起不来
            catch (Exception ex) { Logger.Error("AISummaries 唯一索引创建失败", ex); }

            // 迁移：Activities 增加 UTC 时间双列（2026-08-23）
            // 目的：本地时间字符串在夏令时切换/手动改时钟时会产生重叠或空洞，
            //       统计口径改用 StartTimeUtc/EndTimeUtc 派生，展示仍用本地列。
            try
            {
                // 两个"缺列"标记：先都当缺，扫完表结构再纠正
                bool hasStartUtc = false, hasEndUtc = false;
                // PRAGMA table_info 返回一行一列：cid / name / type / notnull / dflt_value / pk
                using (var infoCmd = new SqliteCommand("PRAGMA table_info(Activities)", conn))
                // reader 挂在 using 链上，出块即释放
                using (var infoReader = infoCmd.ExecuteReader())
                {
                    // 逐列扫描
                    while (infoReader.Read())
                    {
                        // 第 1 列是列名（name）
                        var colName = infoReader.GetString(1);
                        // 命中 StartTimeUtc
                        if (colName == "StartTimeUtc") hasStartUtc = true;
                        // 命中 EndTimeUtc；用 else if 是因为同一列不可能同时叫两个名字
                        else if (colName == "EndTimeUtc") hasEndUtc = true;
                    }
                }
                // 缺列则补：新列是 TEXT 且可空，老行值为 NULL，随后统一回填
                // ALTER ADD COLUMN 只能加可空或有默认值的列，所以这里不能加 NOT NULL
                if (!hasStartUtc)
                    // 补 StartTimeUtc 列（命令对象未显式 Dispose，靠 GC 回收，见风险清单）
                    new SqliteCommand("ALTER TABLE Activities ADD COLUMN StartTimeUtc TEXT", conn).ExecuteNonQuery();
                if (!hasEndUtc)
                    // 补 EndTimeUtc 列
                    new SqliteCommand("ALTER TABLE Activities ADD COLUMN EndTimeUtc TEXT", conn).ExecuteNonQuery();

                // 回填历史行：按"历史值即写入时的本地时间、以当前时区换算"的假设一次性补齐。
                // 局限：历史上发生过时制切换的边界天可能有 ±1 小时偏差（一次性，不会恶化）。
                // strftime 的 'utc' 修饰符含义：把左边的时间值按"本地时间"解释，再换算成 UTC 输出，
                // 所以 StartTime（本地串）→ UTC 串，末尾加 Z 与 Insert 时的写入格式保持一致
                using (var backfill = new SqliteCommand(@"
                    UPDATE Activities
                    SET StartTimeUtc = strftime('%Y-%m-%dT%H:%M:%SZ', StartTime, 'utc'),
                        EndTimeUtc   = strftime('%Y-%m-%dT%H:%M:%SZ', EndTime,  'utc')
                    WHERE StartTimeUtc IS NULL OR EndTimeUtc IS NULL", conn))
                {
                    // WHERE 限定只回填 NULL 行，重复执行无副作用
                    int rows = backfill.ExecuteNonQuery();
                    // 只有真正回填了才记日志，避免每次启动刷屏
                    if (rows > 0) Logger.Info($"UTC 双列迁移：已回填 {rows} 行活动记录");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Activities UTC 列迁移失败", ex);
                throw; // 统计口径依赖该列，失败必须暴露
            }

            // Categories 表为空时插入预置分类（首次运行）
            // 计数命令（这里没套 using，命令对象不显式释放——进程内只跑一次，见风险清单）
            var countCmd = new SqliteCommand("SELECT COUNT(*) FROM Categories", conn);
            // COUNT(*) 不会返回 NULL，?? 0L 只是防御性兜底
            if ((long)(countCmd.ExecuteScalar() ?? 0L) == 0) // 空表才播种
            {
                // 预置分类直接复用 CategoryRepository 的权威定义，避免重复硬编码导致不同步
                // 按数组顺序插入，AUTOINCREMENT 依次分配 Id=1..13，
                // 这正好落在 CategoryRepository.MaxPresetCategoryId(=13) 的"不可删"保护范围内
                foreach (var (name, color, icon, order) in CategoryRepository.PresetCategories)
                {
                    // 参数化插入每个预置分类
                    var insertCat = new SqliteCommand(
                        "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, @Icon, @SortOrder)", conn);
                    // 分类名（业务上的自然键，Activities.Category 存的就是这个字符串）
                    insertCat.Parameters.AddWithValue("@Name", name);
                    // 十六进制颜色，如 #4A90D9
                    insertCat.Parameters.AddWithValue("@Color", color);
                    // 图标键名，如 code / chat，由 UI 层按名映射成图标
                    insertCat.Parameters.AddWithValue("@Icon", icon);
                    // 展示排序号
                    insertCat.Parameters.AddWithValue("@SortOrder", order);
                    // 执行插入；整段运行在初始化锁内，不需要额外事务包裹
                    insertCat.ExecuteNonQuery();
                }
            }

            // Settings 表为空时插入预置设置项（首次运行）
            // 复用上面同一个变量，重新指向新的计数命令
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Settings", conn);
            if ((long)(countCmd.ExecuteScalar() ?? 0L) == 0) // 空表才播种默认设置
            {
                // 从 SettingsRepository.Defaults 读取默认设置写入数据库（单一数据源，不在此处重复硬编码）
                foreach (var kv in SettingsRepository.Defaults)
                {
                    var insertSet = new SqliteCommand(
                        "INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)", conn);
                    // 设置项键名（Key 列有 UNIQUE 约束）
                    insertSet.Parameters.AddWithValue("@Key", kv.Key);
                    // 值统一按字符串存，解析与校验交给调用方
                    insertSet.Parameters.AddWithValue("@Value", kv.Value);
                    // 执行插入
                    insertSet.ExecuteNonQuery();
                }
                // 播种为直写库（绕过 Set），若缓存已存在则强制失效（防御性兜底）
                SettingsRepository.InvalidateCache();
            }

            // Rules 表为空时插入预置分类规则（首次运行）
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Rules", conn);
            if ((long)(countCmd.ExecuteScalar() ?? 0L) == 0) // 空表才播种规则
            {
                // 先查分类名 → Id 映射，后面插入规则要用（规则表外键指向 Categories.Id）
                var catMap = new Dictionary<string, int>();
                // 读全部分类的 Id 与名称
                using (var catQ = new SqliteCommand("SELECT Id, Name FROM Categories", conn))
                // reader 随 using 链释放
                using (var catR = catQ.ExecuteReader())
                    // 逐行填入映射：第 1 列是 Name，第 0 列是 Id
                    while (catR.Read())
                        catMap[catR.GetString(1)] = catR.GetInt32(0);

                // 预置进程规则从 JSON 文件加载（IsCustom=0 表示预置不可删，全部进程名精确匹配）
                // JSON 文件: Data/seed_rules.json
                // 放在 Data 目录是为了让规则清单可以独立于程序集调整，改规则不用重新编译
                var seedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "seed_rules.json");
                var procRuleList = new List<(string proc, string cat)>(); // (进程名, 分类名) 列表
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seedPath))) // 解析 JSON 数组
                    {
                        // 逐个元素取 process / category 两个字段
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var proc = item.GetProperty("process").GetString() ?? ""; // 取进程名
                            var cat = item.GetProperty("category").GetString() ?? ""; // 取目标分类名
                            // 字段缺失时 GetProperty 会抛异常，由外层 catch 兜住并整批跳过
                            procRuleList.Add((proc, cat));
                        }
                    }
                }
                catch (Exception seedEx)
                {
                    // 种子文件缺失/损坏不阻断初始化，仅记日志（用户可后续手动建规则）
                    Logger.Error($"预置规则 JSON 加载失败（{seedPath}），跳过预置规则。用户可在设置中手动添加规则。", seedEx);
                }
                // 逐条插入预置规则，进程名匹配分类名对应的 Id
                foreach (var (proc, cat) in procRuleList)
                {
                    // 分类必须存在才插入；JSON 里写了不存在的分类名会静默丢弃（配合上面 catch 的日志排查）
                    if (catMap.TryGetValue(cat, out int catId)) // 分类必须存在才插入
                    {
                        // TitleKeyword 固定写 NULL（预置规则只按进程名匹配），IsCustom 固定写 0（不可删）
                        using var ins = new SqliteCommand(
                            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@p, NULL, @c, 0)", conn);
                        // 进程名（规则匹配键）
                        ins.Parameters.AddWithValue("@p", proc);
                        // 目标分类 Id（由分类名映射而来）
                        ins.Parameters.AddWithValue("@c", catId);
                        // 执行插入
                        ins.ExecuteNonQuery();
                    }
                }

                // 注意：这里记的是"解析出来的条数"，不是"成功插入的条数"，
                //       被 catMap 过滤掉的条目也会计入，看日志排查时要留意这个差异
                Logger.Info($"预置 {procRuleList.Count} 条分类规则已写入 Rules 表");
            }

            _initialized = true;   // 全部成功后才置初始化标志
            Logger.Info("数据库初始化完成");
        }
        catch (Exception ex)
        {
            // 初始化失败必须暴露给调用方（主窗口启动会弹错），不能静默继续
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
        // 初始化成功即视为连接可用（真实走一遍建表/迁移路径）
        try
        {
            Initialize(); // 内部幂等，失败抛异常
            return true;
        }
        catch
        {
            return false; // 任何异常视为连接不可用
        }
    }
}
