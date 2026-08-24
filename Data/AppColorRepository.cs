// ============================================================================
// AppColorRepository.cs — AppColors 应用专属颜色表的仓储（静态类）
// 职责：按进程名存取自定义颜色（主键=进程名），供 AppColorAllocator 使用。
// ============================================================================
// 泛型集合类型（Dictionary）
using System.Collections.Generic;
// SQLite ADO.NET 提供程序（SqliteConnection/SqliteCommand 等）
using Microsoft.Data.Sqlite;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 应用颜色仓储 — 管理每个应用的自定义颜色（独立于分类颜色）。
/// AppColors 以进程名为 PRIMARY KEY，一个应用至多一条颜色记录；
/// 未命中时可回退到分类颜色或全局默认色（由调用方处理 null）。
/// </summary>
public static class AppColorRepository
{
    // 确保数据库已初始化（首次调用触发建表，幂等）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 获取所有应用颜色，返回 Dictionary&lt;进程名, 颜色&gt;
    /// </summary>
    /// <returns>进程名 → 十六进制颜色值的字典，表为空时返回空字典</returns>
    public static Dictionary<string, string> GetAll()
    {
        // 初始化检查：保证 AppColors 表已存在
        EnsureInit();
        // 结果字典：进程名 → 颜色值
        var dict = new Dictionary<string, string>();
        // 创建连接（指向统一权威连接字符串）
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 打开连接
        conn.Open();
        // 全表查询：只取进程名与颜色两列（按主键序返回，行数=应用数）
        using var cmd = new SqliteCommand("SELECT ProcessName, Color FROM AppColors", conn);
        // 执行查询得到只进只读游标
        using var reader = cmd.ExecuteReader();
        // 逐行读取并填充字典
        while (reader.Read())
        {
            // 第0列=进程名（主键），第1列=颜色值
            dict[reader.GetString(0)] = reader.GetString(1);
        }
        // 返回完整映射
        return dict;
    }

    /// <summary>
    /// 设置某个应用的颜色，存在则更新，不存在则插入（UPSERT）
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="color">十六进制颜色值，如 #FF6B6B</param>
    public static void Set(string processName, string color)
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // ON CONFLICT 主键冲突时更新颜色，实现 UPSERT
        // 单条语句完成“有则改、无则插”，天然原子且省去先查后写
        using var cmd = new SqliteCommand(@"
            INSERT INTO AppColors (ProcessName, Color) VALUES (@p, @c)
            ON CONFLICT(ProcessName) DO UPDATE SET Color=@c", conn);
        // 绑定进程名参数（参数化防注入）
        cmd.Parameters.AddWithValue("@p", processName);
        // 绑定颜色值参数
        cmd.Parameters.AddWithValue("@c", color);
        // 执行写入；UpdatedAt 列由表的 DEFAULT (datetime('now','localtime')) 自动维护
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取某个应用的颜色
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <returns>颜色字符串，不存在则返回 null</returns>
    public static string? Get(string processName)
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 按主键精确查询颜色单列
        using var cmd = new SqliteCommand("SELECT Color FROM AppColors WHERE ProcessName=@p", conn);
        // 绑定进程名参数
        cmd.Parameters.AddWithValue("@p", processName);
        // 执行查询
        using var reader = cmd.ExecuteReader();
        // 有结果行则返回第0列颜色值
        if (reader.Read()) return reader.GetString(0);
        // 无匹配行返回 null，由调用方回退到分类色/默认色
        return null;
    }
}
