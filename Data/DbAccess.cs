// ============================================================================
// DbAccess.cs — SQLite 连接的统一打开入口
// 目的：集中管理连接创建，保证全项目连接字符串来源一致。
// ============================================================================
// 基础类型（本文件当前仅隐式用到，保留以备扩展）
using System;
// SQLite ADO.NET 提供程序：SqliteConnection 等类型
using Microsoft.Data.Sqlite;

// 数据访问层命名空间：所有仓储与数据库基础设施都在此层
namespace TimeActivity.Data;

/// <summary>
/// 内部共享的数据库连接辅助 — 封装“初始化 + 打开已就绪连接”的样板，
/// 供各 Repository 复用，消除每个方法重复的 EnsureInit()+new SqliteConnection+Open 三连。
/// 仅 internal 可见，不对外暴露 API；公开方法签名与数据处理逻辑保持不变。
/// </summary>
/// <remarks>
/// 关于连接复用：本类不搞连接池、也不缓存连接，每次调用都新开一条。
/// SQLite 是进程内嵌数据库，建连接只是打开文件句柄，开销远小于网络数据库，
/// 换来的是"连接生命周期与调用栈严格一致"——不会有意外的跨线程/长事务持有。
/// 连接对象不是线程安全的：谁 Open 谁用，不要在线程之间传递返回值。
/// </remarks>
internal static class DbAccess
{
    /// <summary>
    /// 初始化数据库并返回已打开的连接。
    /// 等价于原各仓储方法开头的 EnsureInit(); new SqliteConnection(DatabaseHelper.ConnectionString); conn.Open();
    /// </summary>
    /// <returns>已打开且可用的 SQLite 连接，调用方应以 using 释放</returns>
    public static SqliteConnection Open()
    {
        // 第一步：确保数据库已初始化（建表/迁移/种子数据）；幂等，重复调用无副作用
        DatabaseHelper.Initialize();
        // 第二步：按统一连接字符串创建连接对象（此时尚未真正打开库文件）
        var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 第三步：真正打开连接（首次 Open 才建立文件句柄；库文件不存在时由引擎自动创建）
        conn.Open();
        // 返回就绪连接；上方任一步抛异常则不会走到这里，由调用方决定如何处理
        // 调用方约定：一律写成 using var conn = DbAccess.Open()，让连接跟方法栈一起释放，
        //            不要在字段里长期持有（WAL 模式下长期持有的连接会拖住检查点，导致 -wal 文件只长不小）
        return conn;
    }
}
