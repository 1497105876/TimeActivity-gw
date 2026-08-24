// ============================================================================
// RuleRepository.cs — Rules 分类规则表的仓储（静态类）
// 职责：规则的增删改查与整体保存(SaveAll)；按进程改分类(UpdateCategory)；
//       清空(ClearAll)；预置规则(IsCustom=0)受保护不可删。
// ============================================================================
// 泛型集合（List、Dictionary）
using System.Collections.Generic;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;
// 数据模型（Rule、RuleItem）
using TimeActivity.Models;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 规则仓储 — 负责 Rules 表的增删查。
/// 规则语义：进程名精确匹配 + 标题关键词可选匹配 → 归入指定分类；
/// IsCustom=0 为预置规则（受保护），1 为用户自定义规则。
/// </summary>
public static class RuleRepository
{
    // 确保数据库已初始化（首次调用触发建表与预置规则播种）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    /// <summary>
    /// 获取全部分类规则，按 Id 排序
    /// </summary>
    /// <returns>规则列表，包含预置规则和自定义规则</returns>
    public static List<Rule> GetAll()
    {
        // 初始化检查：保证 Rules 表已存在
        EnsureInit();
        // 结果容器
        var list = new List<Rule>();
        // 创建连接（指向统一权威连接字符串）
        using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
        // 打开连接
        conn.Open();
        // 查所有规则，按 Id 升序排
        using var cmd = new SqliteCommand("SELECT Id, ProcessName, TitleKeyword, CategoryId, IsCustom FROM Rules ORDER BY Id", conn);
        // 执行查询得到游标
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // 按列序映射到实体
            list.Add(new Rule
            {
                // 自增主键
                Id = reader.GetInt32(0),
                // 进程名
                ProcessName = reader.GetString(1),
                // 标题关键词：NULL 表示不按标题过滤（映射为 null 而非空串）
                TitleKeyword = reader.IsDBNull(2) ? null : reader.GetString(2),
                // 目标分类 Id
                CategoryId = reader.GetInt32(3),
                // 0/1 → bool：是否用户自定义
                IsCustom = reader.GetBoolean(4)
            });
        }
        // 返回全部规则
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
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // IsCustom=1 标记为用户自定义规则，可在设置中删除
        using var cmd = new SqliteCommand(
            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@P, @T, @C, 1)", conn);
        // 绑定进程名参数
        cmd.Parameters.AddWithValue("@P", processName);
        // titleKeyword 为空时存 NULL，匹配逻辑里 NULL 表示不检查标题
        cmd.Parameters.AddWithValue("@T", string.IsNullOrEmpty(titleKeyword) ? (object)System.DBNull.Value : titleKeyword);
        // 绑定目标分类 Id
        cmd.Parameters.AddWithValue("@C", categoryId);
        // 执行插入；不做去重，同进程多条规则由匹配端按顺序取先命中者
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按进程名更新分类。预置规则改为自定义（IsCustom=1），不存在则新建
    /// </summary>
    /// <param name="processName">进程名</param>
    /// <param name="categoryId">新的分类 Id</param>
    public static void UpdateCategory(string processName, int categoryId)
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 更新分类并标记为自定义规则（预置规则改后也变成自定义）
        // 注意：该进程若有 TitleKeyword 不同的多条规则会被一并更新
        using var cmd = new SqliteCommand(
            "UPDATE Rules SET CategoryId=@C, IsCustom=1 WHERE ProcessName=@P", conn);
        // 绑定新分类 Id
        cmd.Parameters.AddWithValue("@C", categoryId);
        // 绑定进程名参数
        cmd.Parameters.AddWithValue("@P", processName);
        // 执行更新并取受影响行数
        int rows = cmd.ExecuteNonQuery();
        if (rows == 0)
        {
            // 该进程名还没有规则，插入一条新的自定义规则
            Insert(processName, null, categoryId);
        }
    }

    /// <summary>
    /// 批量保存规则（更新已有 + 插入新规则），用事务保证原子性
    /// </summary>
    /// <param name="rules">要保存的规则列表，每条含 Id(>0 更新 / <=0 新增)、ProcessName、TitleKeyword、CategoryName、IsCustom</param>
    /// <param name="categoryNameToId">分类名 → 分类 Id 的映射，用于按名称查 Id</param>
    public static void SaveAll(List<RuleItem> rules, Dictionary<string, int> categoryNameToId)
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 开启事务：全部行要么一起生效要么一起回滚
        using var transaction = conn.BeginTransaction();

        try
        {
            // 逐条处理前端提交的规则集合
            foreach (var r in rules)
            {
                // 进程名为空的无效行直接跳过
                if (string.IsNullOrWhiteSpace(r.ProcessName))
                    continue;
                // 分类名在映射中找不到（如分类已被删除）也跳过，避免悬挂引用
                if (!categoryNameToId.TryGetValue(r.CategoryName, out int catId))
                    continue;

                // 已有正数 Id 的行走更新分支
                if (r.Id > 0)
                {
                    using var upd = new SqliteCommand(
                        "UPDATE Rules SET CategoryId=@c, IsCustom=@ic WHERE Id=@id", conn, transaction);
                    // 新分类 Id
                    upd.Parameters.AddWithValue("@c", catId);
                    // 自定义标记（bool → 0/1）
                    upd.Parameters.AddWithValue("@ic", r.IsCustom ? 1 : 0);
                    // 主键定位
                    upd.Parameters.AddWithValue("@id", r.Id);
                    upd.ExecuteNonQuery();
                }
                // 无有效 Id 的行走新增分支
                else
                {
                    using var ins = new SqliteCommand(
                        "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@p, @k, @c, @ic)", conn, transaction);
                    // 进程名兜底空串防 NULL 入库
                    ins.Parameters.AddWithValue("@p", r.ProcessName ?? "");
                    // 标题关键词可为 NULL（NULL=不按标题过滤）
                    ins.Parameters.AddWithValue("@k", (object?)r.TitleKeyword ?? DBNull.Value);
                    // 目标分类 Id
                    ins.Parameters.AddWithValue("@c", catId);
                    // 自定义标记（bool → 0/1）
                    ins.Parameters.AddWithValue("@ic", r.IsCustom ? 1 : 0);
                    ins.ExecuteNonQuery();
                }
            }

            // 全部成功后一次性提交事务
            transaction.Commit();
        }
        // 任一步失败：整体回滚保持规则表原状，并把异常抛给上层提示
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 清空所有自定义规则（只删 IsCustom=1 的，预置规则保留）
    /// </summary>
    public static void ClearAll()
    {
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 只删自定义规则，IsCustom=0 的预置规则不动
        using var cmd = new SqliteCommand("DELETE FROM Rules WHERE IsCustom=1", conn);
        // 执行删除；IsCustom 列无索引时为全表扫描（表很小，可接受）
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // 规则指纹（2026-08-23 新增）：用于"规则是否变化"的低成本判断，
    // 让启动/保存设置时只在规则真正变化后才执行 全量重分类+总结失效。
    // ========================================================================

    /// <summary>
    /// 计算当前规则集的指纹（SHA-256 前 16 字节的十六进制）。
    /// 参与指纹的字段：ProcessName、TitleKeyword、CategoryId、IsCustom，按 Id 排序保证稳定。
    /// </summary>
    public static string ComputeFingerprint()
    {
        // 逐行收集指纹原料：Id|进程|标题词|分类|自定义标记
        var lines = new List<string>();
        // 打开连接（using 嵌套确保命令/读取器随连接一起释放）
        using (var conn = DbAccess.Open())
        // 按 Id 排序保证同样内容产生同样字节序列；IFNULL 把 NULL 标题词归一为空串参与哈希
        using (var cmd = new SqliteCommand(
            "SELECT Id, ProcessName, IFNULL(TitleKeyword,''), CategoryId, IsCustom FROM Rules ORDER BY Id", conn))
        // 执行查询
        using (var r = cmd.ExecuteReader())
        {
            // 每条规则拼一行“|”分隔字符串
            while (r.Read())
                lines.Add($"{r.GetInt64(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetInt64(3)}|{r.GetInt64(4)}");
        }
        // 无规则时也给出稳定指纹（空串哈希），保证"清空规则"同样能被检测到
        // 创建 SHA-256 实例（using 及时释放非托管资源）
        using var sha = System.Security.Cryptography.SHA256.Create();
        // 对以 \n 连接后的 UTF-8 字节计算哈希
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        // 十六进制输出缓冲区（每字节展开为 2 个字符）
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        // 逐字节追加两位小写十六进制
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        // 截取前 128 位作为指纹返回，长度适中且碰撞概率可忽略
        return sb.ToString()[..32]; // 128 位足够防碰撞
    }

    /// <summary>读取设置表中存储的上次指纹；从未记录过返回 null。</summary>
    public static string? GetStoredFingerprint()
    {
        // 从设置表读取固定键 RulesFingerprint（未设置时得到空串）
        var v = SettingsRepository.Get("RulesFingerprint", "");
        // 空/未设置一律归一为 null，语义即“从未记录过”
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>把当前指纹写入设置表（在完成重分类后调用）。</summary>
    public static void StoreFingerprint()
    {
        // 实时计算指纹并落库，标记“当前规则已同步处理”
        SettingsRepository.Set("RulesFingerprint", ComputeFingerprint());
    }

    /// <summary>
    /// 判断规则是否相对上次记录发生了变化（不落库，仅比对）。
    /// </summary>
    public static bool HasChangedSinceStored()
    {
        // 读取上次指纹（null 表示从未记录过）
        var stored = GetStoredFingerprint();
        // 从未记录过，或与实时计算的当前指纹不同，都判定“有变化”
        return stored == null || stored != ComputeFingerprint();
    }
}
