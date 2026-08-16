using System;
using Microsoft.Data.Sqlite;

namespace TimeActivity.Data;

/// <summary>
/// 内部共享的数据库连接辅助 — 封装“初始化 + 打开已就绪连接”的样板，
/// 供各 Repository 复用，消除每个方法重复的 EnsureInit()+new SqliteConnection+Open 三连。
/// 仅 internal 可见，不对外暴露 API；公开方法签名与数据处理逻辑保持不变。
/// </summary>
internal static class DbAccess
{
    /// <summary>
    /// 初始化数据库并返回已打开的连接。
    /// 等价于原各仓储方法开头的 EnsureInit(); new SqliteConnection(DatabaseHelper.ConnectionString); conn.Open();
    /// </summary>
    public static SqliteConnection Open()
    {
        DatabaseHelper.Initialize();
        var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        return conn;
    }
}
