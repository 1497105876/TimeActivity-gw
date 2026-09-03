// ============================================================================
// DailySummaryRepository.cs — 每日预聚合三张表的仓储（静态类）
// 职责：生成（GenerateAllMissing / GenerateForDate）与查询（GetDailyTotals /
//       GetCategorySummary / GetProcessSummary）。
// 为什么要有预聚合：统计页查"最近 30 天"如果直接扫 Activities，要遍历几万到几十万行；
//       汇总表按天/分类/进程各存一行，同样的区间只扫几十行。
// 三张表：DailyTotal（每天一行）、DailyCategorySummary（每天×分类一行）、
//         DailyProcessSummary（每天×进程一行，一个进程只保留时长最长的那个分类）。
// 生成时机：程序启动时 GenerateAllMissing 补历史缺口，每天 23:59 自动生成当天。
// 一致性约定：汇总永远是"可由 Activities 重算出来的派生数据"，
//       所以出问题时的兜底手段是删掉重算，而不是去修汇总表。
// ============================================================================
// 基础类型（DateTime、Exception）
using System;
// 泛型集合（List、Dictionary）
using System.Collections.Generic;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;
// 日志服务
using TimeActivity.Services;
// 帮助扩展（ToDateKey）
using TimeActivity.Helpers;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 每日汇总仓储 — 预聚合三张表：DailyTotal / DailyCategorySummary / DailyProcessSummary
/// 生成时机：程序启动时补昨天 + 每天 23:59 自动生成当天
/// 查询时统计页读汇总表而非 Activities 原始表，大幅减少扫描行数
/// </summary>
public static class DailySummaryRepository
{
    // 确保数据库已初始化（首次调用触发建表，幂等）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 扫描 Activities 表，找出有数据但 DailyTotal 里没记录的日期，全部补生成
    /// </summary>
    /// <remarks>
    /// 典型触发时机：程序启动（补昨天、补上次没跑到 23:59 就关机的那天）、
    /// 以及 CleanOldData 删了明细但汇总还在/或反过来之后。
    /// 幂等：补过之后 DailyTotal 里就有了日期，下次再跑 NOT IN 就查不到，不会重复生成。
    /// </remarks>
    public static void GenerateAllMissing()
    {
        using var conn = DbAccess.Open(); // 统一连接入口（内部已含 Initialize）

        // 找出 Activities 里有但 DailyTotal 里没有的日期——即缺失汇总的日期
        // 反连接查询：NOT IN 子查询排除已有汇总的日期；DISTINCT+ORDER BY 得到升序去重列表
        // 用 NOT IN 的前提：子查询列 DailyTotal.Date 是主键、绝不为 NULL。
        // 一旦子查询里出现 NULL，SQL 的 NOT IN 会整体求值为 UNKNOWN，导致一条都补不出来——
        // 这是个很容易踩的坑，改动表结构时要留意
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT date(StartTimeUtc,'localtime') as D 
            FROM Activities 
            WHERE date(StartTimeUtc,'localtime') NOT IN (SELECT Date FROM DailyTotal)
            ORDER BY D";
        var missingDates = new List<string>(); // 缺失日期列表
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            // 这里用 GetString：如果某行 StartTimeUtc 为 NULL（迁移漏回填），
            // date() 得到 NULL，GetString 会抛 InvalidCastException 并中断整个补生成流程
            missingDates.Add(reader.GetString(0));
        // 先关游标，后面才好用同一连接写库
        reader.Close();

        // 逐天补生成汇总，复用同一连接避免每天重开，失败只记日志不中断
        // transaction 传 null ⇒ 每天各自是一个隐式事务：
        // 某天失败不会影响其它天，但那一天的三张表可能只写了一半（靠下次重跑修复）
        foreach (var d in missingDates)
        {
            try { GenerateForDate(d, conn, null); }
            // 吞掉单天异常：补生成属于启动阶段的"尽力而为"动作，不该因为它让程序起不来
            catch (Exception ex) { Logger.Error($"补生成汇总失败: {d}", ex); }
        }

        // 一条都不缺时不刷日志，避免每次启动都记一条
        if (missingDates.Count > 0)
            Logger.Info($"已补生成 {missingDates.Count} 天的汇总数据");
    }

    /// <summary>
    /// 生成某天的汇总数据（写入三张表：DailyTotal / DailyCategorySummary / DailyProcessSummary）
    /// </summary>
    /// <param name="date">日期字符串，格式 yyyy-MM-dd</param>
    /// <remarks>便捷重载：自己开连接、自己释放，供定时任务/手动触发使用。</remarks>
    public static void GenerateForDate(string date)
    {
        // 连接随方法结束释放
        using var conn = DbAccess.Open();
        // 转发到带连接参数的完整实现；不传事务 ⇒ 每条语句各自隐式提交
        GenerateForDate(date, conn, null);
    }

    /// <summary>
    /// 生成某天的汇总数据（可在外部事务内执行，保证原子性）
    /// </summary>
    /// <param name="date">日期字符串，格式 yyyy-MM-dd</param>
    /// <param name="conn">已打开的数据库连接</param>
    /// <param name="transaction">外部事务（可选），传入则在该事务内执行</param>
    /// <remarks>
    /// 整体思路（三步，每步都是"先算/先清，再写"）：
    ///   1) DailyTotal：一条聚合 SQL 同时算出总时长与活跃时长，UPSERT 写一行；
    ///   2) DailyCategorySummary：先按日期整段 DELETE，再把"当天×分类"的聚合结果逐条 INSERT；
    ///   3) DailyProcessSummary：同上，但一个进程在同一天有多分类时只保留时长最长的那条
    ///      （因为表的主键是 Date+ProcessName，放不下多行）。
    /// 事务语义：transaction 为 null 时每条语句各自隐式提交，
    ///          中途失败会留下"删了旧的没插新的"的半截状态，靠重跑修复；
    ///          传了事务则整体原子（ReclassifyAll 就是这么用的）。
    /// 入参 date 必须是 yyyy-MM-dd，才能和 SQL 里 date(StartTimeUtc,'localtime') 的输出对上。
    /// </remarks>
    public static void GenerateForDate(string date, SqliteConnection conn, SqliteTransaction? transaction = null)
    {
        // 1. DailyTotal — 计算当天总时长和活跃时长（排除空闲）
        // 每个命令都要单独挂事务：SqliteCommand 不会自动继承连接上的当前事务
        using var totalCmd = conn.CreateCommand();
        // 有外部事务就加入，没有就不设（走连接的隐式事务）
        if (transaction != null) totalCmd.Transaction = transaction;
        // COALESCE 防 NULL，第二个 SUM 用 CASE WHEN 过滤空闲时长
        // 不带 GROUP BY 的聚合查询恒返回一行，当天无数据时是 0 而不是空结果集
        totalCmd.CommandText = "SELECT COALESCE(SUM(Duration),0), COALESCE(SUM(CASE WHEN IsIdle=0 THEN Duration ELSE 0 END),0) FROM Activities WHERE date(StartTimeUtc,'localtime')=@date";
        // 日期键参数
        totalCmd.Parameters.AddWithValue("@date", date);
        using var totalReader = totalCmd.ExecuteReader();
        long totalSeconds = 0, totalActive = 0; // 总时长 / 活跃时长
        // 恒有一行；if 只是形式上的保险
        if (totalReader.Read())
        {
            totalSeconds = totalReader.GetInt64(0);  // SUM(Duration) 全部
            totalActive = totalReader.GetInt64(1);   // 非空闲部分之和
        }
        // 读完立刻关游标，下一步才能在同连接上写 DailyTotal
        totalReader.Close();

        // UPSERT：日期已存在则更新，否则插入
        // 依赖 DailyTotal.Date 是 PRIMARY KEY，ON CONFLICT(Date) 才能命中
        using var upsertTotal = conn.CreateCommand();
        if (transaction != null) upsertTotal.Transaction = transaction;
        upsertTotal.CommandText = @"INSERT INTO DailyTotal (Date, TotalActiveSeconds, TotalSeconds)
            VALUES (@date, @active, @total)
            ON CONFLICT(Date) DO UPDATE SET TotalActiveSeconds=@active, TotalSeconds=@total, CreatedAt=datetime('now','localtime')";
        upsertTotal.Parameters.AddWithValue("@date", date);
        upsertTotal.Parameters.AddWithValue("@active", totalActive);
        upsertTotal.Parameters.AddWithValue("@total", totalSeconds);
        // 执行写入；CreatedAt 只在冲突分支里被刷新，首次插入走列的 DEFAULT
        upsertTotal.ExecuteNonQuery();

        // 2. DailyCategorySummary — 按类别汇总（先删旧数据再插入新的）
        // 走"整段删 + 整段插"而不是 UPDATE：分类集合会变，比对差异再更新的代价不比全量重写小
        using var delCat = conn.CreateCommand();
        if (transaction != null) delCat.Transaction = transaction;
        // 清掉该日全部分类行；Date 是复合主键前缀，能走索引
        delCat.CommandText = "DELETE FROM DailyCategorySummary WHERE Date=@date";
        delCat.Parameters.AddWithValue("@date", date);
        delCat.ExecuteNonQuery();

        using var catCmd = conn.CreateCommand();
        if (transaction != null) catCmd.Transaction = transaction;
        // 按分类汇总时长，只统计非空闲记录
        // 注意：这里只排除空闲，不做其它过滤，所以"空闲"这个分类本身不会被写进汇总
        catCmd.CommandText = @"SELECT Category, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTimeUtc,'localtime')=@date AND IsIdle=0 GROUP BY Category";
        catCmd.Parameters.AddWithValue("@date", date);
        // 先读到内存，再批量写入（避免 reader 打开时执行命令导致 SQLite 报错）
        var catRows = new List<(string cat, long sec)>();
        using (var catReader = catCmd.ExecuteReader())
        {
            while (catReader.Read())
                catRows.Add((catReader.GetString(0), catReader.GetInt64(1)));
        }
        // 出块后游标已释放，可以安全写入
        foreach (var (cat, sec) in catRows)
        {
            using var insCat = conn.CreateCommand();
            if (transaction != null) insCat.Transaction = transaction;
            insCat.CommandText = "INSERT INTO DailyCategorySummary (Date, Category, Seconds) VALUES (@d, @c, @s)";
            insCat.Parameters.AddWithValue("@d", date);
            insCat.Parameters.AddWithValue("@c", cat);
            insCat.Parameters.AddWithValue("@s", sec);
            // 用 INSERT 而非 UPSERT：上一行刚删干净，理论上不会撞主键
            insCat.ExecuteNonQuery();
        }

        // 3. DailyProcessSummary — 按进程汇总
        using var delProc = conn.CreateCommand();
        if (transaction != null) delProc.Transaction = transaction;
        // 同样先整段清掉该日的进程行
        delProc.CommandText = "DELETE FROM DailyProcessSummary WHERE Date=@date";
        delProc.Parameters.AddWithValue("@date", date);
        delProc.ExecuteNonQuery();

        using var procCmd = conn.CreateCommand();
        if (transaction != null) procCmd.Transaction = transaction;
        // 按进程名+分类汇总，一个进程可能出现多个分类，取时长最长的那个
        // ORDER BY ProcessName, Total DESC 是关键：它让"同进程内时长最大的分类"排在最前面，
        // 下面的去重循环才能靠"第一次见到就写入"取到最大值
        procCmd.CommandText = @"SELECT ProcessName, Category, SUM(Duration) as Total FROM Activities 
            WHERE date(StartTimeUtc,'localtime')=@date AND IsIdle=0 GROUP BY ProcessName, Category
            ORDER BY ProcessName, Total DESC";
        procCmd.Parameters.AddWithValue("@date", date);
        // 同样先读到内存，同进程取时长最长的类别（主键是 Date+ProcessName）
        var procRows = new List<(string proc, string cat, long sec)>();
        using (var procReader = procCmd.ExecuteReader())
        {
            while (procReader.Read())
                procRows.Add((procReader.GetString(0), procReader.GetString(1), procReader.GetInt64(2)));
        }
        // 同进程只保留时长最大的那条（SQL 已按 Total DESC 排序，第一个就是最大的）
        // 统计口径提示：被丢掉的那些次要分类的秒数就彻底不进汇总表了，
        // 所以"各进程 Seconds 之和"会小于 DailyTotal 的活跃总秒数——这是设计取舍，不是 bug，
        // 要看分进程的完整分布请直接查 ActivityRepository.GetProcessSummaryByRange
        var seen = new HashSet<string>();
        foreach (var (proc, cat, sec) in procRows)
        {
            if (!seen.Add(proc)) continue; // 已有该进程，跳过
            using var insProc = conn.CreateCommand();
            if (transaction != null) insProc.Transaction = transaction;
            insProc.CommandText = "INSERT INTO DailyProcessSummary (Date, ProcessName, Category, Seconds) VALUES (@d, @p, @c, @s)";
            insProc.Parameters.AddWithValue("@d", date);
            insProc.Parameters.AddWithValue("@p", proc);
            // 该进程当天的"主分类"= 时长最长的那个分类
            insProc.Parameters.AddWithValue("@c", cat);
            // 只记主分类对应的秒数
            insProc.Parameters.AddWithValue("@s", sec);
            insProc.ExecuteNonQuery();
        }

        // 每天生成都记一条日志；一天 1 条，量不大，方便排查"汇总到底跑没跑"
        Logger.Info($"每日汇总已生成：{date}，总活跃 {totalActive} 秒");
    }

    /// <summary>
    /// 查询日期范围内的每日总活跃时长（趋势图用）
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <param name="includeIdle">是否包含空闲时间：true 读 TotalSeconds 列，false 读 TotalActiveSeconds 列</param>
    /// </summary>
    public static Dictionary<string, int> GetDailyTotals(DateTime start, DateTime end, bool includeIdle = false)
    {
        EnsureInit();
        var result = new Dictionary<string, int>(); // 结果：日期 → 秒数
        // 按开关选列（白名单拼接，非用户输入）：两个列名都是本文件内的固定字面量，不存在注入面
        string col = includeIdle ? "TotalSeconds" : "TotalActiveSeconds"; // 按开关选列（白名单拼接，非用户输入）
        // 这里用的是裸 SqliteConnection 而不是 DbAccess.Open()（上面已显式 EnsureInit，功能等价）
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 打开连接
        conn.Open();
        using var cmd = new SqliteCommand(
            $"SELECT Date, {col} FROM DailyTotal WHERE Date >= @Start AND Date <= @End ORDER BY Date", conn); // 日期升序
        // 走 Date 主键的范围扫描（Date 是 TEXT 主键，即按字典序排序，等价于日期序）
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey()); // 统一 yyyy-MM-dd 键
        // 闭区间上界
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1); // 填充字典
        // 只返回有汇总记录的天；没跑过汇总的日期不会出现，图表层要自己补零
        return result;
    }

    /// <summary>
    /// 查询日期范围内按类别汇总（类别占比用）
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <returns>字典：分类名 → 总秒数，按时长降序</returns>
    public static Dictionary<string, int> GetCategorySummary(DateTime start, DateTime end)
    {
        // 初始化检查：保证三张汇总表已存在
        EnsureInit();
        // 结果：分类名 → 秒数
        var result = new Dictionary<string, int>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 打开连接
        conn.Open();
        // 直接在预聚合表上做二次聚合：扫描行数 = 天数 × 分类数，比扫 Activities 小两三个数量级
        // Date 是复合主键 (Date, Category) 的前缀，范围条件能走索引
        using var cmd = new SqliteCommand(
            @"SELECT Category, SUM(Seconds) as Total FROM DailyCategorySummary 
              WHERE Date >= @Start AND Date <= @End GROUP BY Category ORDER BY Total DESC", conn);
        // 起始日期键（含）
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        // 结束日期键（含）
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            // 第0列=分类名，第1列=区间内累计秒数
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 无数据返回空字典
        return result;
    }

    /// <summary>
    /// 查询日期范围内按进程汇总（Top应用用）
    /// </summary>
    /// <param name="start">起始日期</param>
    /// <param name="end">结束日期</param>
    /// <param name="categoryFilter">可选分类过滤；为空则统计全部分类</param>
    /// <returns>字典：进程名 → 总秒数，按时长降序</returns>
    public static Dictionary<string, int> GetProcessSummary(DateTime start, DateTime end, string? categoryFilter = null)
    {
        // 初始化检查：保证三张汇总表已存在
        EnsureInit();
        var result = new Dictionary<string, int>(); // 结果：进程名 → 秒数
        // 分类过滤片段：为空时不加条件，否则追加 AND Category=@Cat（固定字面量拼接，非用户输入）
        string filter = string.IsNullOrEmpty(categoryFilter) ? "" : " AND Category=@Cat"; // 可选分类过滤片段
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 打开连接
        conn.Open();
        // 口径提醒：DailyProcessSummary 里每个进程每天只存了"时长最长的那个主分类"，
        // 所以按 Category 过滤时，一个进程在其它次要分类下的时长不会被算进来，
        // 结果会偏小；要看不受此限制的口径请用 ActivityRepository.GetProcessSummaryByRange
        using var cmd = new SqliteCommand(
            $@"SELECT ProcessName, SUM(Seconds) as Total FROM DailyProcessSummary 
              WHERE Date >= @Start AND Date <= @End{filter} GROUP BY ProcessName ORDER BY Total DESC", conn);
        // 起始日期键（含）
        cmd.Parameters.AddWithValue("@Start", start.ToDateKey());
        // 结束日期键（含）
        cmd.Parameters.AddWithValue("@End", end.ToDateKey());
        // 只有加了过滤片段才绑定 @Cat，否则多传参数会让 SQLite 报"没有该参数"
        if (!string.IsNullOrEmpty(categoryFilter))
            cmd.Parameters.AddWithValue("@Cat", categoryFilter);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            // 第0列=进程名，第1列=区间内累计秒数
            result[reader.GetString(0)] = reader.GetInt32(1);
        // 无数据返回空字典
        return result;
    }
}
