// ============================================================================
// AISummaryRepository.cs — AISummaries 表的仓储（静态类）
// 职责：AI 总结的写入/查询/存在性检查；manual 与 auto 双来源管理；
//       InvalidateRecent 使近期总结失效以触发重算。
// 依赖唯一索引 UX_AISummaries_Type(Date,SummaryType,AutoType) 防重复。
// ============================================================================
// 基础类型（DateTime）
using System;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;
// 日志服务
using TimeActivity.Services;
// 帮助扩展（ToDateKey）
using TimeActivity.Helpers;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// AI 总结仓储 — 负责 AISummaries 表的增删查。
/// 一天同一 SummaryType 下 manual 与 auto 各至多一条（先删后插 + 唯一索引双保险）。
/// </summary>
public static class AISummaryRepository
{
    // 确保数据库已初始化（首次调用触发建表/迁移，幂等）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 插入一条 AI 总结记录（同日期同类型同来源的旧记录会被先删再插）
    /// </summary>
    /// <param name="date">总结对应的日期</param>
    /// <param name="summaryText">总结正文内容</param>
    /// <param name="summaryType">总结类型，默认 daily（每日总结）</param>
    /// <param name="autoType">来源类型：manual（手动生成）或 auto（自动生成）</param>
    public static void Insert(DateTime date, string summaryText, string summaryType = "daily", string autoType = "manual")
    {
        // 初始化检查：保证 AISummaries 表存在且 AutoType 列已迁移到位
        EnsureInit();

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();

        // 用事务包住 DELETE+INSERT，中途失败不会丢数据
        // 这里没有写 try/catch：任何异常向外抛时，using 会在栈展开过程中 Dispose 掉 transaction，
        // SqliteTransaction.Dispose 对未提交的事务执行回滚，所以"删了旧的却没插新的"不会发生
        using var transaction = conn.BeginTransaction();

        // 先删同类型同日期同来源的旧记录，再插入新的，保证每次只保留最新一条
        // 过滤三元组正好对应唯一索引 UX_AISummaries_Type 的列组合
        using var delCmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE Date=@Date AND SummaryType=@SummaryType AND AutoType=@AutoType", conn, transaction);
        // 日期键 yyyy-MM-dd
        delCmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        // 总结类型（daily/weekly/monthly）
        delCmd.Parameters.AddWithValue("@SummaryType", summaryType);
        // 来源（manual/auto）
        delCmd.Parameters.AddWithValue("@AutoType", autoType);
        // 执行删除旧记录
        delCmd.ExecuteNonQuery();

        // 插入语句：五列齐全，CreatedAt 由参数显式给当前时间
        const string sql = @"INSERT INTO AISummaries (Date, SummaryText, SummaryType, AutoType, CreatedAt)
            VALUES (@Date, @SummaryText, @SummaryType, @AutoType, @CreatedAt)";

        // 创建插入命令并挂到同一事务
        using var cmd = new SqliteCommand(sql, conn, transaction);
        // 日期统一用 yyyy-MM-dd 格式存储
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        // 总结正文（可能较长，TEXT 列无上限约束）
        cmd.Parameters.AddWithValue("@SummaryText", summaryText);
        // 总结类型
        cmd.Parameters.AddWithValue("@SummaryType", summaryType);
        // 来源类型
        cmd.Parameters.AddWithValue("@AutoType", autoType);
        // 生成时间=当前本地时间（毫秒精度），查询端按它取“最新一条”
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        // 执行插入
        cmd.ExecuteNonQuery();
        // 删+插整体提交，保证原子生效
        transaction.Commit();
    }

    /// <summary>
    /// 检查某天是否已有自动生成的 AI 总结
    /// </summary>
    /// <param name="date">要检查的日期</param>
    /// <param name="summaryType">总结类型（如 daily）</param>
    /// <returns>已存在自动总结返回 true，否则 false</returns>
    public static bool HasAuto(DateTime date, string summaryType)
    {
        // 初始化检查：保证表结构就绪
        EnsureInit();
        // 查 AutoType='auto' 的记录数量，大于 0 说明已有自动总结
        // 命中条件走唯一索引前缀，代价极低
        const string sql = "SELECT COUNT(*) FROM AISummaries WHERE Date=@Date AND SummaryType=@Type AND AutoType='auto'";
        // 打开就绪连接
        using var conn = DbAccess.Open();
        // 创建计数命令
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定日期键
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        // 绑定总结类型
        cmd.Parameters.AddWithValue("@Type", summaryType);
        // SQLite 的 COUNT(*) 以 64 位整数返回
        long count = (long)cmd.ExecuteScalar()!;
        // 数量大于 0 即存在自动总结
        return count > 0;
    }

    /// <summary>
    /// 使"近期"的自动总结失效（删除 auto 来源记录），下一次 GenerateMissingAsync 会重新生成。
    /// 用于底层活动数据被修改（如重新分类）后让已有总结刷新。仅删 auto，保留用户手动总结。
    /// 覆盖范围：最近 7 天日报 + 最近一个完整周 + 最近一个完整月（与 SummaryScheduler 的补算窗口一致）。
    /// </summary>
    public static void InvalidateRecent()
    {
        // 初始化检查：保证表结构就绪
        EnsureInit();
        // 以“今天”为基准推导各失效窗口
        var today = DateTime.Today;
        // 打开就绪连接
        // 三次 DELETE 各是独立语句，没有包在同一事务里：
        // 中途失败会留下"日报清了但周报没清"的部分失效状态，下次调用会补齐，不追求强一致
        using var conn = DbAccess.Open();

        // 删最近 7 天日报（auto）
        using (var cmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE AutoType='auto' AND SummaryType='daily' AND Date >= @From", conn))
        {
            // 窗口起点=7 天前的日期键（闭区间，含当天共约 8 天）
            cmd.Parameters.AddWithValue("@From", today.AddDays(-7).ToDateKey());
            // 执行删除
            cmd.ExecuteNonQuery();
        }
        // 删最近一个完整周（auto）
        using (var cmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE AutoType='auto' AND SummaryType='weekly' AND Date=@Ws", conn))
        {
            // 精确命中“最近一个已结束自然周”的周一日期键（weekly 记录以周一起始日为主键语义）
            cmd.Parameters.AddWithValue("@Ws", DateHelper.GetLatestClosedWeekStart().ToDateKey());
            // 执行删除
            cmd.ExecuteNonQuery();
        }
        // 删最近一个完整月（auto）
        using (var cmd = new SqliteCommand(
            "DELETE FROM AISummaries WHERE AutoType='auto' AND SummaryType='monthly' AND Date=@Ms", conn))
        {
            // 精确命中“最近一个已结束自然月”的 1 号日期键
            cmd.Parameters.AddWithValue("@Ms", DateHelper.GetLatestClosedMonthStart().ToDateKey());
            // 执行删除
            cmd.ExecuteNonQuery();
        }
        // 记录失效动作日志（后续调度器会自动补算）
        Logger.Info("已使近期 AI 自动总结失效，将在下次检查时重新生成");
    }

    /// <summary>
    /// 获取某天最新一条 AI 总结文本（不限来源类型）
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <param name="summaryType">总结类型，默认 daily</param>
    /// <returns>总结文本，没有则返回 null</returns>
    public static string? Get(DateTime date, string summaryType = "daily")
    {
        // 初始化检查：保证表结构就绪
        EnsureInit();
        // 按创建时间降序取第一条，即最新的一条
        // 不区分 AutoType：manual 与 auto 中取 CreatedAt 较新者
        const string sql = "SELECT SummaryText FROM AISummaries WHERE Date = @Date AND SummaryType = @Type ORDER BY CreatedAt DESC LIMIT 1";

        // 打开就绪连接
        using var conn = DbAccess.Open();
        // 创建查询命令
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定日期键
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        // 绑定总结类型
        cmd.Parameters.AddWithValue("@Type", summaryType);

        // 标量查询：无记录返回 null，有则返回文本
        var result = cmd.ExecuteScalar();
        // 安全转型：非字符串结果一律视为 null
        return result as string;
    }

    /// <summary>
    /// 获取 AI 总结（带 AutoType 过滤），返回 (内容, 生成时间)
    /// </summary>
    /// <param name="date">要查询的日期</param>
    /// <param name="summaryType">总结类型（daily/weekly/monthly）</param>
    /// <param name="autoType">来源过滤：manual 或 auto</param>
    /// <returns>元组：总结文本与生成时间字符串，均可能为 null</returns>
    public static (string? summary, string? createdAt) GetWithMeta(DateTime date, string summaryType, string autoType)
    {
        // 初始化检查：保证表结构就绪
        EnsureInit();
        // 同一 (Date, Type, Auto) 组合按唯一索引约束至多一行，仍加排序兜底防脏数据
        const string sql = "SELECT SummaryText, CreatedAt FROM AISummaries WHERE Date = @Date AND SummaryType = @Type AND AutoType = @Auto ORDER BY CreatedAt DESC LIMIT 1";

        // 打开就绪连接
        using var conn = DbAccess.Open();
        // 创建查询命令
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定日期键
        cmd.Parameters.AddWithValue("@Date", date.ToDateKey());
        // 绑定总结类型
        cmd.Parameters.AddWithValue("@Type", summaryType);
        // 绑定来源过滤
        cmd.Parameters.AddWithValue("@Auto", autoType);

        // 执行查询
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            // 第0列=总结正文
            string text = reader.GetString(0);
            // 第1列=生成时间，理论非空但防御性判 NULL
            string? createdAt = reader.IsDBNull(1) ? null : reader.GetString(1);
            // 返回内容与时间元组
            return (text, createdAt);
        }
        // 无记录：双 null 元组
        return (null, null);
    }
}
