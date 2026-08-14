using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TimeActivity.Models;

namespace TimeActivity.Data;

/// <summary>
/// 规则仓储 — 负责 Rules 表的增删查
/// </summary>
public static class RuleRepository
{
    // 确保数据库已初始化
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 获取全部分类规则，按 Id 排序
    /// </summary>
    /// <returns>规则列表，包含预置规则和自定义规则</returns>
    public static List<Rule> GetAll()
    {
        EnsureInit();
        var list = new List<Rule>();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        // 查所有规则，按 Id 升序排
        using var cmd = new SqliteCommand("SELECT Id, ProcessName, TitleKeyword, CategoryId, IsCustom FROM Rules ORDER BY Id", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Rule
            {
                Id = reader.GetInt32(0),
                ProcessName = reader.GetString(1),
                TitleKeyword = reader.IsDBNull(2) ? null : reader.GetString(2),
                CategoryId = reader.GetInt32(3),
                IsCustom = reader.GetBoolean(4)
            });
        }
        return list;
    }

    /// <summary>
    /// 插入一条自定义分类规则（IsCustom=1，用户可删除）
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="titleKeyword">窗口标题关键词（可为空，空表示只按进程名匹配）</param>
    /// <param name="categoryId">分类 Id</param>
    public static void Insert(string processName, string titleKeyword, int categoryId)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        // IsCustom=1 标记为用户自定义规则，可在设置中删除
        using var cmd = new SqliteCommand(
            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@P, @T, @C, 1)", conn);
        cmd.Parameters.AddWithValue("@P", processName);
        // titleKeyword 为空时存 NULL，匹配逻辑里 NULL 表示不检查标题
        cmd.Parameters.AddWithValue("@T", string.IsNullOrEmpty(titleKeyword) ? (object)System.DBNull.Value : titleKeyword);
        cmd.Parameters.AddWithValue("@C", categoryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按进程名更新分类。预置规则改为自定义（IsCustom=1），不存在则新建
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="categoryId">新的分类 Id</param>
    public static void UpdateCategory(string processName, int categoryId)
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        // 更新分类并标记为自定义规则（预置规则改后也变成自定义）
        using var cmd = new SqliteCommand(
            "UPDATE Rules SET CategoryId=@C, IsCustom=1 WHERE ProcessName=@P", conn);
        cmd.Parameters.AddWithValue("@C", categoryId);
        cmd.Parameters.AddWithValue("@P", processName);
        int rows = cmd.ExecuteNonQuery();
        if (rows == 0)
        {
            // 该进程名还没有规则，插入一条新的自定义规则
            Insert(processName, null, categoryId);
        }
    }

    /// <summary>
    /// 清空所有自定义规则（只删 IsCustom=1 的，预置规则保留）
    /// </summary>
    public static void ClearAll()
    {
        EnsureInit();
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        conn.Open();
        // 只删自定义规则，IsCustom=0 的预置规则不动
        using var cmd = new SqliteCommand("DELETE FROM Rules WHERE IsCustom=1", conn);
        cmd.ExecuteNonQuery();
    }
}
