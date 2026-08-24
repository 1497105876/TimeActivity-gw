// ============================================================================
// ActivityRepository.cs — Activities 活动明细表的仓储（静态类）
// 职责：活动记录插入/按日查询/区间聚合（分类、进程、每日总量）；
//       GetUsedProcessNames 供设置页规则管理展示"用过的应用"。
// 查询均以 StartTime 的 date()/区间为过滤条件，依赖三个索引加速。
// ============================================================================
// 基础类型（DateTime）
using System;
// 泛型集合（List、Dictionary、HashSet）
using System.Collections.Generic;
// 固定文化解析（时间字符串与存储格式严格往返）
using System.Globalization;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;
// 数据模型（ActivityRecord）
using TimeActivity.Models;
// 日志服务
using TimeActivity.Services;
// 帮助扩展（ToDateKey）
using TimeActivity.Helpers;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 活动记录仓储 — 负责 Activities 表的增删查。
/// 时间口径：本地列（StartTime/EndTime）供展示，UTC 双列（StartTimeUtc/EndTimeUtc）
/// 供统计派生，两者在 Insert 时成对写入；聚合过滤统一走 UTC 列换算的本地日期。
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
        // 确保库与表已就绪
        EnsureInit();
        // 插入活动记录并取回自增 Id
        // 2026-08-23 UTC 双列：本地时间列照旧（展示用），同时写入 UTC 列（统计口径抗时区/改钟）
        // 批语句：INSERT 之后紧跟 SELECT last_insert_rowid() 返回新主键
        const string sql = @"
            INSERT INTO Activities (ProcessName, WindowTitle, Category, StartTime, EndTime, StartTimeUtc, EndTimeUtc, Duration, IsIdle, CreatedAt)
            VALUES (@ProcessName, @WindowTitle, @Category, @StartTime, @EndTime, @StartTimeUtc, @EndTimeUtc, @Duration, @IsIdle, @CreatedAt);
            SELECT last_insert_rowid();";

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 创建插入命令
        using var cmd = new SqliteCommand(sql, conn);
        // 进程名（如 chrome.exe），采集端保证非空
        cmd.Parameters.AddWithValue("@ProcessName", activity.ProcessName);
        // 窗口标题，null 归一为空串避免参数空引用
        cmd.Parameters.AddWithValue("@WindowTitle", activity.WindowTitle ?? "");
        // 分类名（字符串快照；重分类由 ReclassifyAll 批量回写）
        cmd.Parameters.AddWithValue("@Category", activity.Category);
        // 时间统一用 yyyy-MM-dd HH:mm:ss.fff 格式存储，精确到毫秒
        cmd.Parameters.AddWithValue("@StartTime", activity.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("@EndTime", activity.EndTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        // UTC 列：ISO8601 带 Z 后缀，由本地值换算而来
        cmd.Parameters.AddWithValue("@StartTimeUtc", activity.StartTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        cmd.Parameters.AddWithValue("@EndTimeUtc", activity.EndTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        // 时长（秒）
        cmd.Parameters.AddWithValue("@Duration", activity.Duration);
        // IsIdle 存为 0/1，SQLite 没有布尔类型
        cmd.Parameters.AddWithValue("@IsIdle", activity.IsIdle ? 1 : 0);
        // 入库时间=当前本地时间（毫秒精度）
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        // 执行批命令：标量结果即 last_insert_rowid() 的值
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 查询某天所有活动记录，按开始时间排序
    /// </summary>
    /// <param name="date">要查询的日期（只看日期部分，不看时间）</param>
    /// <returns>该天所有活动记录列表，按 StartTime 升序</returns>
    public static List<ActivityRecord> GetByDate(DateTime date)
    {
        // 确保库与表已就绪
        EnsureInit();
        // 结果容器
        var result = new List<ActivityRecord>();
        // 入参转 yyyy-MM-dd 键（忽略时间部分）
        string dateStr = date.ToDateKey();
        // 用 date(StartTime) 提取日期部分做比较，省去时间部分的干扰
        // 口径说明：过滤基于 UTC 列换算出的本地日历日，跨零点归属与汇总统计一致
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') = @DateStr
            ORDER BY StartTime";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定日期参数
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        // 执行查询并逐行映射实体
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // 对象初始化器按列序取值
            result.Add(new ActivityRecord
            {
                // 自增主键
                Id = reader.GetInt64(0),
                // 进程名
                ProcessName = reader.GetString(1),
                // 窗口标题
                WindowTitle = reader.GetString(2),
                // 分类名
                Category = reader.GetString(3),
                // 本地开始时间（固定文化解析，与写入格式严格对应）
                StartTime = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                // 本地结束时间
                EndTime = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                // 时长（秒）
                Duration = reader.GetInt32(6),
                // 0/1 → bool
                IsIdle = reader.GetInt32(7) == 1
            });
        }

        // 返回该日全部明细
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
        // 确保库与表已就绪
        EnsureInit();
        // 结果容器
        var result = new List<ActivityRecord>();
        // StartTime >= start AND StartTime < end，左闭右开区间
        // 2026-08-23：改用 UTC 列做范围比较（入参为本地时间，内部换算），抗时区/改钟
        // 字符串比较成立的前提是两端都为同格式 ISO 串（见下方参数格式化）
        const string sql = @"
            SELECT Id, ProcessName, WindowTitle, Category, StartTime, EndTime, Duration, IsIdle
            FROM Activities
            WHERE StartTimeUtc >= @Start AND StartTimeUtc < @End
            ORDER BY StartTimeUtc";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 下界：本地→UTC 的 ISO 串（含）；命中 IX_Activities_StartTime 同构列序可走索引
        cmd.Parameters.AddWithValue("@Start", start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        // 上界：不含（左闭右开）
        cmd.Parameters.AddWithValue("@End", end.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        // 执行查询并逐行映射实体
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // 对象初始化器按列序取值
            result.Add(new ActivityRecord
            {
                // 自增主键
                Id = reader.GetInt64(0),
                // 进程名
                ProcessName = reader.GetString(1),
                // 窗口标题
                WindowTitle = reader.GetString(2),
                // 分类名
                Category = reader.GetString(3),
                // 本地开始时间（展示用，从本地列还原）
                StartTime = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                // 本地结束时间
                EndTime = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                // 时长（秒）
                Duration = reader.GetInt32(6),
                // 0/1 → bool
                IsIdle = reader.GetInt32(7) == 1
            });
        }

        // 返回区间明细
        return result;
    }

    /// <summary>
    /// 按分类汇总某天活动时长（排除空闲时间）
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <returns>字典：分类名 → 总秒数，按时长降序排列</returns>
    public static Dictionary<string, int> GetCategorySummaryByDate(DateTime date)
    {
        // 确保库与表已就绪
        EnsureInit();
        // 结果字典：分类名 → 秒数
        var result = new Dictionary<string, int>();
        // 日期键 yyyy-MM-dd
        string dateStr = date.ToDateKey();
        // 按分类汇总时长，排除空闲记录，按总时长降序排
        // SUM(Duration) 对当日全部非空闲行求和；GROUP BY Category 一组一行
        const string sql = @"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') = @DateStr AND IsIdle = 0
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定日期参数
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        // 逐行写入字典（第0列=分类名，第1列=总秒数）
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 返回当日分类汇总
        return result;
    }

    /// <summary>
    /// 按进程名汇总某天活动时长（排除空闲时间）
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <returns>字典：进程名 → 总秒数，按时长降序排列</returns>
    public static Dictionary<string, int> GetProcessSummaryByDate(DateTime date)
    {
        // 确保库与表已就绪
        EnsureInit();
        // 结果字典：进程名 → 秒数
        var result = new Dictionary<string, int>();
        // 日期键 yyyy-MM-dd
        string dateStr = date.ToDateKey();
        // 按进程名汇总时长，排除空闲记录，按总时长降序排
        const string sql = @"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') = @DateStr AND IsIdle = 0
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定日期参数
        cmd.Parameters.AddWithValue("@DateStr", dateStr);

        // 逐行写入字典（第0列=进程名，第1列=总秒数）
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 返回当日进程汇总
        return result;
    }

    /// <summary>
    /// 按分类汇总日期范围内的时长，默认排除空闲
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：分类名 → 总秒数，按时长降序</returns>
    // 便捷重载：固定排除空闲记录，转发到带 includeIdle 参数的完整实现
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
        // 确保库与表已就绪
        EnsureInit();
        // 结果字典：分类名 → 秒数
        var result = new Dictionary<string, int>();
        // includeIdle=false 时追加 IsIdle=0 过滤条件
        // 片段来自固定白名单字符串拼接，无注入风险
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        // 用 date(StartTime) 取日期部分做范围比较
        // 注意：对列包 date() 函数会使 IX_Activities_StartTime 失效，区间大时为全表扫描聚合
        string sql = $@"
            SELECT Category, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') >= @Start AND date(StartTimeUtc,'localtime') <= @End{idleFilter}
            GROUP BY Category
            ORDER BY TotalSeconds DESC";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 起始日期键（闭区间下界）
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        // 结束日期键（闭区间上界）
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());

        // 逐行收集聚合结果
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 返回区间分类汇总
        return result;
    }

    /// <summary>
    /// 按进程名汇总日期范围内的时长，默认排除空闲
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：进程名 → 总秒数，按时长降序</returns>
    // 便捷重载：固定排除空闲记录，转发到带 includeIdle 参数的完整实现
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
        // 确保库与表已就绪
        EnsureInit();
        // 结果字典：进程名 → 秒数
        var result = new Dictionary<string, int>();
        // 与分类版相同的空闲过滤片段拼接逻辑
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        // 按进程名汇总，GROUP BY ProcessName，按总时长降序
        string sql = $@"
            SELECT ProcessName, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') >= @Start AND date(StartTimeUtc,'localtime') <= @End{idleFilter}
            GROUP BY ProcessName
            ORDER BY TotalSeconds DESC";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 起始日期键（含）
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        // 结束日期键（含）
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());

        // 逐行收集聚合结果
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 返回区间进程汇总
        return result;
    }

    /// <summary>
    /// 获取日期范围内每天的活跃时长合计，默认排除空闲
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：日期字符串(yyyy-MM-dd) → 总秒数，按日期升序</returns>
    // 便捷重载：固定排除空闲记录，转发到带 includeIdle 参数的完整实现
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
        // 确保库与表已就绪
        EnsureInit();
        // 结果字典：日期 → 秒数
        var result = new Dictionary<string, int>();
        // 与其他聚合一致的空闲过滤片段拼接
        string idleFilter = includeIdle ? "" : " AND IsIdle = 0";
        // 按日期分组汇总，GROUP BY date(StartTime)，用于趋势图展示
        // GROUP BY / ORDER BY 直接引用别名 Date（SQLite 允许），输出天然按日期升序
        string sql = $@"
            SELECT date(StartTimeUtc,'localtime') AS Date, SUM(Duration) AS TotalSeconds
            FROM Activities
            WHERE date(StartTimeUtc,'localtime') >= @Start AND date(StartTimeUtc,'localtime') <= @End{idleFilter}
            GROUP BY Date
            ORDER BY Date";

        // 打开就绪连接并创建命令
        using var conn = DbAccess.Open();
        using var cmd = new SqliteCommand(sql, conn);
        // 起始日期键（含）
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        // 结束日期键（含）
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());

        // 逐行收集“日期 → 秒数”
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 返回逐日总量（无数据的天不会出现在字典中，由图表层补零）
        return result;
    }

    /// <summary>
    /// 获取所有用户实际使用过的进程名（去重）
    /// </summary>
    public static HashSet<string> GetUsedProcessNames()
    {
        // 忽略大小写去重，避免同名不同大小写的进程被重复列出
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 统一走 DbAccess.Open()，它会先确保数据库已初始化（建表），
        // 避免本方法作为首个查询时 Activities 表尚不存在而抛 no such table
        using var conn = DbAccess.Open();
        // 排除空闲记录和占位符 "(空闲)"
        // DISTINCT 由引擎去重；配合忽略大小写集合双重保险
        using var cmd = new SqliteCommand(
            "SELECT DISTINCT ProcessName FROM Activities WHERE IsIdle = 0", conn);
        // 执行查询
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // 取原始进程名
            var name = reader.GetString(0);
            // 过滤掉空进程名和 "(空闲)" 占位符
            if (!string.IsNullOrEmpty(name) && name != "(空闲)")
                result.Add(name);
        }
        // 返回去重后的进程名集合
        return result;
    }
}
