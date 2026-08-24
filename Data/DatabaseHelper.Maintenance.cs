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
        using var conn = new SqliteConnection(ConnectionString);
        // 打开连接后执行在线备份
        conn.Open();
        // 创建备份命令
        using var cmd = conn.CreateCommand();
        // 目标路径单引号翻倍转义，防 SQL 引号截断；注意 VACUUM INTO 要求目标文件不存在，否则报错
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
            // 顺序无依赖（无外键级联），任意顺序等价
            string[] tables = { "Activities", "Screenshots", "DailyTotal", "DailyCategorySummary", "DailyProcessSummary", "AISummaries" };
            // 逐表执行整表 DELETE
            foreach (var table in tables)
            {
                // 表名来自上方固定白名单数组，非用户输入，拼接安全
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
        using var transaction = conn.BeginTransaction();

        try
        {
            // 取所有非空闲活动记录，读到内存里再批量更新（避免 reader 打开时执行命令）
            using var selCmd = new SqliteCommand(
                "SELECT Id, ProcessName, WindowTitle FROM Activities WHERE IsIdle=0", conn, transaction);
            // 执行查询（游标保持打开直至读完）
            using var reader = selCmd.ExecuteReader();

            // 待更新集合：(主键, 新分类名) 二元组列表
            var updates = new List<(long id, string category)>(); // 待更新集合
            while (reader.Read()) // 逐条读取并在内存中重算分类
            {
                long id = reader.GetInt64(0);      // 记录主键
                string proc = reader.GetString(1); // 进程名
                string title = reader.IsDBNull(2) ? "" : reader.GetString(2); // 标题可能为 NULL
                string newCat = classifyFunc(proc, title); // 用最新规则计算新分类
                updates.Add((id, newCat));
            }

            reader.Close(); // 先关 reader 再批量写（SQLite 不允许同时读写）

            foreach (var (id, cat) in updates) // 批量回写新分类
            {
                using var updCmd = new SqliteCommand(
                    "UPDATE Activities SET Category=@c WHERE Id=@id", conn, transaction);
                updCmd.Parameters.AddWithValue("@c", cat);
                updCmd.Parameters.AddWithValue("@id", id);
                updated += updCmd.ExecuteNonQuery();
            }

            // 重新生成每日汇总（在同一个事务内）
            using var datesCmd = new SqliteCommand(
                "SELECT DISTINCT date(StartTimeUtc,'localtime') FROM Activities", conn, transaction); // UTC 派生业务日期（2026-08-23）
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
        Initialize(); // 确保库/表存在
        // 计算截止时间：超过这个时间的数据将被清理
        string cutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd HH:mm:ss"); // 明细表用完整时间戳
        string dateCutoff = DateTime.Now.AddDays(-retentionDays).ToDateKey();                 // 汇总/总结表按日期
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
