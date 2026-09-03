// ============================================================================
// ScreenshotRepository.cs — Screenshots 截图索引表的仓储（静态类）
// 职责：写入截图索引（Insert）、按时间区间取最近一张（GetForTimeRange）、
//       按文件路径删索引行（DeleteByPath，配合物理文件清理）。
// 分层约定：本类只管索引，不管物理文件。文件的创建由 ScreenshotService 负责，
// 文件的删除由容量控制 / CleanOldData 负责，两边不是原子的，
// 所以"文件没了但索引还在"是可能出现的正常中间态，GetForTimeRange 用 File.Exists 兜住。
// ============================================================================
// 基础类型（DateTime、AppDomain 等）
using System;
// 文件路径与删除操作（Path/File）
using System.IO;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 截图记录仓储 — 负责 Screenshots 表的增删查。
/// 本表只存截图文件的索引信息（路径/大小/时间），物理文件由清理逻辑另行删除；
/// 读取时若发现文件已不存在会返回 null（索引行留待 CleanOldData 统一清理）。
/// </summary>
public static class ScreenshotRepository
{
    // 确保数据库已初始化（首次调用触发建表，幂等）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 按文件路径删除截图记录（2026-09-02 H4 修复新增）。
    /// 供截图清理逻辑在删除物理文件后同步清理索引行，避免 Screenshots 表
    /// 残留"文件已删、行还在"的幻影行。
    /// </summary>
    /// <param name="fullPath">截图文件的绝对路径</param>
    /// <remarks>
    /// 表中 FilePath 存储形式有两种：程序目录内的截图存相对路径、
    /// 自定义目录的截图存绝对路径（见 ScreenshotService.CaptureAndSave 的入库逻辑），
    /// 因此按 绝对路径 + 程序目录相对路径 两种形式一并删除。
    /// </remarks>
    public static void DeleteByPath(string fullPath)
    {
        // 初始化检查：保证 Screenshots 表已存在
        EnsureInit();
        // 程序目录前缀（与 CaptureAndSave 入库时的相对化规则保持一致）
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        // 绝对路径 → 相对路径（程序目录内的截图）；目录外保持绝对路径（两种形式相同）
        // 大小写不敏感比较：Windows 路径大小写不敏感，但库里可能存着不同写法的路径
        string relPath = fullPath.StartsWith(appDir, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(appDir.Length)
            : fullPath;

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 两种存储形式任一命中即删除
        // 没有索引也没关系：FilePath 列上没有索引，这是全表扫描，但清理是低频操作
        using var cmd = new SqliteCommand(
            "DELETE FROM Screenshots WHERE FilePath = @Abs OR FilePath = @Rel", conn);
        // 绝对路径形式
        cmd.Parameters.AddWithValue("@Abs", fullPath);
        // 相对路径形式（相对路径与绝对路径相同时，两个参数值一样，等价于单条件删除）
        cmd.Parameters.AddWithValue("@Rel", relPath);
        // 执行删除；删不到行也不报错（幂等，重复删无副作用）
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 插入一条截图记录，返回新记录的自增 Id
    /// </summary>
    /// <param name="filePath">截图文件路径（相对路径或绝对路径）</param>
    /// <param name="fileSize">文件大小（字节）</param>
    /// <returns>新插入记录的自增 Id</returns>
    public static long Insert(string filePath, long fileSize)
    {
        // 初始化检查：保证 Screenshots 表已存在
        EnsureInit();
        // 多语句批：INSERT 之后紧跟 SELECT last_insert_rowid() 取回自增主键
        const string sql = @"
            INSERT INTO Screenshots (FilePath, CapturedAt, FileSize, CreatedAt)
            VALUES (@FilePath, @CapturedAt, @FileSize, @CreatedAt);
            SELECT last_insert_rowid();";

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 创建插入命令
        using var cmd = new SqliteCommand(sql, conn);
        // CapturedAt 和 CreatedAt 都用当前时间，精确到毫秒
        // 绑定截图文件路径参数（相对路径/绝对路径都接受；本方法不校验文件是否真的存在，
        // 存在性判断统一推迟到读取时——插入不该因为文件还没落完盘而失败）
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        // 捕获时间=当前本地时间（yyyy-MM-dd HH:mm:ss.fff）
        // 严格说这是"登记时间"而不是"真正按下快门的那一刻"，两者通常只差几十毫秒；
        // 下面 CreatedAt 又单独取了一次 DateTime.Now，两个时间戳可能有毫秒级差异
        cmd.Parameters.AddWithValue("@CapturedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        // 文件大小（字节），由调用方用 FileInfo.Length 传入；供容量统计与上限控制使用，
        // 传 0 也不会报错，但会让容量统计低估（调用方保证不为 0）
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        // 入库时间=当前本地时间；与 CapturedAt 基本一致，保留两列以区分业务时间与落库时间
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        // 执行批命令，标量结果即新记录的自增 Id
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 查找指定时间段内的截图（截图时间必须在活动的开始~结束之间），返回最近一张的路径
    /// </summary>
    /// <param name="startTime">活动开始时间（包含）</param>
    /// <param name="endTime">活动结束时间（包含）</param>
    /// <returns>截图文件的绝对路径，没有则返回 null</returns>
    public static string? GetForTimeRange(DateTime startTime, DateTime endTime)
    {
        // 初始化检查：保证 Screenshots 表已存在
        EnsureInit();
        // 查捕获时间在活动时间范围内的最近一张截图
        // 双闭区间 [Start, End]；ORDER BY CapturedAt DESC + LIMIT 1 只取最新一条，
        // CapturedAt 上的索引 IX_Screenshots_CapturedAt 可加速范围过滤
        const string sql = @"
            SELECT FilePath FROM Screenshots
            WHERE CapturedAt >= @Start AND CapturedAt <= @End
            ORDER BY CapturedAt DESC LIMIT 1";

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();           // 连接随方法结束释放
        // 创建查询命令
        using var cmd = new SqliteCommand(sql, conn); // 命令复用该连接
        // 起始边界：格式化到毫秒，与写入格式一致保证字符串比较正确
        // 这两个参数是本地时间字符串，和 CapturedAt 列（本地时间）同口径，没有做 UTC 换算
        cmd.Parameters.AddWithValue("@Start", startTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        // 结束边界：同上
        cmd.Parameters.AddWithValue("@End", endTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        // 标量查询：命中返回 FilePath 字符串，未命中返回 null
        var result = cmd.ExecuteScalar();
        // 有结果才做路径归一与存在性校验
        // result != DBNull.Value 是必要的：ExecuteScalar 对空行集返回 null，对 NULL 列返回 DBNull
        if (result != null && result != DBNull.Value)
        {
            // 取出数据库中存的路径（可能为相对路径）
            string path = (string)result;
            // 相对路径拼接程序目录，绝对路径直接使用
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            // 文件不存在则返回 null（可能已被容量/过期清理删除，索引行稍后由清理逻辑回收）
            if (File.Exists(path))
                return path;
        }
        // 时间段内无截图或物理文件已丢失
        return null;
    }
}
