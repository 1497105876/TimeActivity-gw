// ============================================================================
// DatabaseHelper.Maintenance.cs — 数据库运维操作部分类
// 职责：
//   1) BackupTo：VACUUM INTO 在线备份（无需停引擎）；
//   2) ClearAllData：事务化清空用户数据（保留分类/规则/设置/颜色）；
//   3) ReclassifyAll：规则变更后全量重算历史活动分类并重建每日汇总；
//   4) CleanOldData：按保留天数清理过期数据（含物理截图文件）。
// 设计要点：批量写操作一律使用事务，失败回滚保证一致性。
// ============================================================================
// 基础类型（Exception、AppDomain、Func）
using System;
// 文件操作（Path/File）
using System.IO;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;
// 日志服务
using TimeActivity.Services;
// 帮助扩展（ToDateKey）
using TimeActivity.Helpers;

// 数据访问层命名空间（与 DatabaseHelper.cs 同属一个 partial 类）
namespace TimeActivity.Data;

/// <summary>
/// DatabaseHelper 的“维护操作”分部 — 负责备份、清空、重新分类、清理旧数据等运维类功能。
/// 与 DatabaseHelper.cs（初始化与建表）共同组成一个 static partial class，公开 API 完全不变。
/// </summary>
public static partial class DatabaseHelper
{
    /// <summary>
    /// 备份数据库到指定路径（使用 VACUUM INTO，不需要停引擎，在线备份）
    /// </summary>
    /// <param name="targetPath">备份文件保存路径</param>
    public static void BackupTo(string targetPath)
    {
        // VACUUM INTO 相当于在线导出一个干净的副本，不需要停数据库
        // 顺带的效果：备份文件是经过碎片整理、索引重建的紧凑版本，通常比原库小
        // 这里没有调 Initialize()——和 ClearAllData/ReclassifyAll 不同，
        // 因为库文件不存在时 Open 会自动建一个空库，VACUUM 照样能导出（得到一个空的合法库）
        using var conn = new SqliteConnection(ConnectionString);
        // 打开连接后执行在线备份
        conn.Open();
        // 创建备份命令
        using var cmd = conn.CreateCommand();
        // 目标路径单引号翻倍转义，防 SQL 引号截断；注意 VACUUM INTO 要求目标文件不存在，否则报错
        // 这是 SQLite 的硬性保护：目标文件已存在时直接失败，不会静默覆盖，
        // 所以调用方要自己保证路径唯一（例如带上时间戳）或先删除旧文件
        cmd.CommandText = $"VACUUM INTO '{targetPath.Replace("'", "''")}'";
        // 执行备份（期间业务读写可不中断，WAL 模式下生成一致性快照）
        cmd.ExecuteNonQuery();
        // 记录备份成功日志
        Logger.Info($"数据库备份到 {targetPath}");
    }

    /// <summary>
    /// 清空所有用户数据（活动记录、截图、AI总结、每日汇总），保留设置和分类
    /// </summary>
    public static void ClearAllData()
    {
        Initialize(); // 确保库/表存在（空库也能安全执行）
        // 创建连接
        using var conn = new SqliteConnection(ConnectionString);
        // 打开连接
        conn.Open();

        // 用事务包住所有 DELETE，中途失败不会丢部分数据
        using var transaction = conn.BeginTransaction();
        try
        {
            // 按表逐个清空，不删 Categories/Settings/AppColors/Rules
            // 保留逻辑：分类/规则/设置/应用颜色属于"配置"，用户清数据时期望它们留下；
            //           被清的六张表属于"使用痕迹"与"由痕迹派生的数据"
            // 顺序无依赖（无外键级联），任意顺序等价
            string[] tables = { "Activities", "Screenshots", "DailyTotal", "DailyCategorySummary", "DailyProcessSummary", "AISummaries" };
            // 逐表执行整表 DELETE
            // 注意：只清库行，不删 Screenshots 对应的物理截图文件，
            //       所以执行后磁盘上会留下没有索引指向的孤儿文件（见风险清单）
            foreach (var table in tables)
            {
                // 表名来自上方固定白名单数组，非用户输入，拼接安全
                // DELETE 不带 WHERE：整表删，SQLite 不重建文件，空间只是标记为空闲页，
                // 想真正缩小文件体积得再跑一次 VACUUM（备份路径下不需要，VACUUM INTO 自带整理）
                using var cmd = new SqliteCommand($"DELETE FROM {table}", conn, transaction);
                // 执行删除并挂到事务
                cmd.ExecuteNonQuery();
            }
            // 全部成功后一次性提交
            transaction.Commit();
        }
        // 任一删除失败：回滚到清空前的完整状态再向上抛
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
        Initialize(); // 确保库/表存在
        int updated = 0; // 更新计数
        // 创建连接
        using var conn = new SqliteConnection(ConnectionString);
        // 打开连接
        conn.Open();

        // 用事务包裹整个操作，失败时回滚保证数据一致性
        // 整段操作耗时与历史数据量成正比（全表读 + 全表 UPDATE + 重建所有天的汇总），
        // 期间会一直占着写锁，所以调用方应放在后台线程，别卡 UI
        using var transaction = conn.BeginTransaction();

        try
        {
            // 取所有非空闲活动记录，读到内存里再批量更新（避免 reader 打开时执行命令）
            // 只挑 IsIdle=0：空闲记录的分类由采集端直接写"空闲"，不参与规则匹配
            using var selCmd = new SqliteCommand(
                "SELECT Id, ProcessName, WindowTitle FROM Activities WHERE IsIdle=0", conn, transaction);
            // 执行查询（游标保持打开直至读完）
            using var reader = selCmd.ExecuteReader();

            // 待更新集合：(主键, 新分类名) 二元组列表
            // 全部历史行进内存——数据量按"每 3 秒一条"估算会很大，这里是本方法最主要的内存开销点
            var updates = new List<(long id, string category)>(); // 待更新集合
            while (reader.Read()) // 逐条读取并在内存中重算分类
            {
                long id = reader.GetInt64(0);      // 记录主键
                string proc = reader.GetString(1); // 进程名
                // WindowTitle 列有 NOT NULL DEFAULT ''，但老库/迁移数据仍可能是 NULL，这里防御性判空
                string title = reader.IsDBNull(2) ? "" : reader.GetString(2); // 标题可能为 NULL
                // 用最新规则计算新分类；classifyFunc 由调用方注入（通常是 CategoryService 的规则匹配器）
                string newCat = classifyFunc(proc, title); // 用最新规则计算新分类
                updates.Add((id, newCat));
            }

            // 先关 reader 再批量写：SQLite 在同一连接上不允许"边读边写"同一张表
            reader.Close(); // 先关 reader 再批量写（SQLite 不允许同时读写）

            // 逐条 UPDATE：没用批量/预处理复用，行数多时语句解析开销会累积（见风险清单）
            foreach (var (id, cat) in updates) // 批量回写新分类
            {
                using var updCmd = new SqliteCommand(
                    "UPDATE Activities SET Category=@c WHERE Id=@id", conn, transaction);
                // 新分类名（字符串快照，直接覆盖旧值）
                updCmd.Parameters.AddWithValue("@c", cat);
                // 主键定位
                updCmd.Parameters.AddWithValue("@id", id);
                // 累加受影响行数；分类没变的行也会被 UPDATE 并计入，所以 updated 是"处理行数"而非"变更行数"
                updated += updCmd.ExecuteNonQuery();
            }

            // 重新生成每日汇总（在同一个事务内）
            // 分类变了，DailyCategorySummary / DailyProcessSummary 的旧数据就失效了，必须整段重算
            // date(StartTimeUtc,'localtime') 取的是 UTC 列换算回本地的日历日，与写入/统计口径一致
            using var datesCmd = new SqliteCommand(
                "SELECT DISTINCT date(StartTimeUtc,'localtime') FROM Activities", conn, transaction); // UTC 派生业务日期（2026-08-23）
            using var dateReader = datesCmd.ExecuteReader();
            // 有活动记录的日期列表
            var dates = new List<string>();
            while (dateReader.Read())
                // 日期键形如 yyyy-MM-dd；若某行 StartTimeUtc 为 NULL（迁移遗漏）会得到 NULL 值
                dates.Add(dateReader.GetString(0));
            // 同样先关游标再写
            dateReader.Close();

            // 逐天重新生成汇总，全部在事务内完成
            foreach (var date in dates)
                // 复用同一连接与事务，保证"重分类 + 重算汇总"整体原子
                DailySummaryRepository.GenerateForDate(date, conn, transaction);

            // 全部步骤成功才落盘；此前任何异常都走下面的回滚
            transaction.Commit();

            // updated=0 时说明没有任何非空闲记录，没必要刷日志
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
        Initialize(); // 确保库/表存在
        // 计算截止时间：超过这个时间的数据将被清理
        // retentionDays 没有做下限校验：传 0 会把截止点设成"当前时刻"，等于清空全部历史，
        // 负数同理；实际取值来自设置项 DataRetentionDays（默认 90），由设置页负责限制
        // 注意这里取了两次 DateTime.Now，理论上存在两次取值跨过整秒/整天的极小概率，
        // 会让明细表与汇总表的清理边界差一点（后果可忽略，重跑即对齐）
        string cutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd HH:mm:ss"); // 明细表用完整时间戳
        string dateCutoff = DateTime.Now.AddDays(-retentionDays).ToDateKey();                 // 汇总/总结表按日期
        int totalDeleted = 0; // 累计删除行数（跨表汇总，用于返回与日志）

        // 整段清理没有开事务：每步各自隐式提交。
        // 取舍：清一删到底的操作中途失败后重跑即可补齐，不值得为它长期占写锁；
        // 代价是可能出现"物理文件已删、索引行还在"或"索引行已删、文件还在"的中间态（见风险清单）
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 1. 清 Activities——删除过期的活动记录
        // 这里用的是本地时间列 StartTime（不是 UTC 列），与下面汇总表按 Date 键、AI 表按 Date 键的口径略有差异：
        // 跨零点那一段可能"明细还在、当天汇总已删"，靠 DailySummaryRepository.GenerateAllMissing 下次补回来
        using var cmd1 = new SqliteCommand("DELETE FROM Activities WHERE StartTime < @Cutoff", conn);
        // 字符串比较：库里是 yyyy-MM-dd HH:mm:ss.fff，这里是 yyyy-MM-dd HH:mm:ss，
        // 同前缀下短串更小，比较结果符合"早于截止时间"的预期
        cmd1.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd1.ExecuteNonQuery();

        // 2. 清 Screenshots——先查出文件路径删物理文件，再删数据库记录
        // 顺序不能反：先把路径读出来，否则库行一删就找不到该删哪个文件了
        using var cmd2 = new SqliteCommand("SELECT FilePath FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd2.Parameters.AddWithValue("@Cutoff", cutoff);
        using (var reader = cmd2.ExecuteReader())
        {
            while (reader.Read())
            {
                try
                {
                    // 相对路径拼接程序目录，绝对路径直接用（库里两种形式都可能存在）
                    var p = reader.GetString(0);
                    string fullPath = Path.IsPathRooted(p) ? p : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p);
                    // 文件可能早就被容量控制删掉了，先判断存在性避免抛异常
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }
                // 单个文件删失败（被占用/无权限）不影响其余文件，仅记日志
                catch (Exception ex) { Logger.Error($"删除旧截图文件失败", ex); }
            }
        }
        // 上面 reader 出块后释放游标，这里才能对同一张表执行写操作
        using var cmd3 = new SqliteCommand("DELETE FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd3.Parameters.AddWithValue("@Cutoff", cutoff);
        // 不管物理文件删没删成功，索引行都一并清掉——删不掉的文件会成为磁盘上的孤儿
        totalDeleted += cmd3.ExecuteNonQuery();

        // 3. 清 AISummaries——手动总结超期删除，自动总结永久保留
        // 业务约定：auto 是系统生成的，可以随时再算，但删了会触发重复调用 AI；
        //           manual 是用户手动生成的，属于用户数据，才需要跟着保留期一起清
        using var cmd4 = new SqliteCommand("DELETE FROM AISummaries WHERE Date < @DateCutoff AND AutoType='manual'", conn);
        cmd4.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd4.ExecuteNonQuery();

        // 4. 清每日汇总三张表——按日期删除过期数据
        // Date 是 TEXT 主键/复合主键前缀，字符串比较即字典序比较，yyyy-MM-dd 格式天然等价于日期比较
        using var cmd5a = new SqliteCommand("DELETE FROM DailyTotal WHERE Date < @DateCutoff", conn);
        cmd5a.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5a.ExecuteNonQuery();

        // 按分类的每日汇总
        using var cmd5b = new SqliteCommand("DELETE FROM DailyCategorySummary WHERE Date < @DateCutoff", conn);
        cmd5b.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5b.ExecuteNonQuery();

        // 按进程的每日汇总
        using var cmd5c = new SqliteCommand("DELETE FROM DailyProcessSummary WHERE Date < @DateCutoff", conn);
        cmd5c.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd5c.ExecuteNonQuery();

        // 没删到东西就不记日志，避免定时任务每天刷一条空日志
        if (totalDeleted > 0)
            Logger.Info($"数据清理：共删除 {totalDeleted} 条旧数据（含活动/截图/AI总结/每日汇总）");

        // 返回删除总数供调用方展示
        return totalDeleted;
    }
}
