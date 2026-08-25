// ============================================================================
// SettingsRepository.cs — Settings 键值设置表的仓储（静态类）
// 职责：Get/Set/GetAll/Delete；Defaults 内置全部默认值；
//       GetDefaultsByPage 支持设置页"按页恢复默认"。
// 所有值均以字符串存储，调用方自行解析与校验。
// ============================================================================
// 基础类型（IEnumerable 等）
using System;
// 泛型集合（Dictionary、KeyValuePair）
using System.Collections.Generic;
// SQLite ADO.NET 提供程序
using Microsoft.Data.Sqlite;

// 数据访问层命名空间
namespace TimeActivity.Data;

/// <summary>
/// 设置仓储 — 负责 Settings 表的读写。
/// Settings 为 Key UNIQUE 的键值表；所有值按字符串存取，
/// 类型解析（int/bool）与合法性校验由调用方负责。
/// </summary>
public static class SettingsRepository
{
    // 确保数据库已初始化（首次调用触发建表与默认值播种）
    private static void EnsureInit() => DatabaseHelper.Initialize();

    // ==================== 内存缓存（2026-08-25 内存优化） ====================
    // 背景：Get() 原实现每次调用都新建 SQLite 连接+命令查库，而截图服务每次截图调 3 次、
    // AI 服务每次调用调 4 次、渲染路径也频繁触发——产生大量短期对象推高 GC 压力。
    // 设置值本身几乎不变（仅设置页保存时更新），全量内存缓存只需几 KB，收益显著。
    // 缓存字典为库的镜像：值 null 表示库中该键为 NULL（区别于"键不存在"）。
    private static Dictionary<string, string?>? _cache;
    private static readonly object _cacheLock = new();

    /// <summary>载入（或复用）设置缓存快照。首次调用走单次全表查询。</summary>
    private static Dictionary<string, string?> LoadCache()
    {
        lock (_cacheLock)
        {
            if (_cache != null) return _cache;
            var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
            using var conn = DbAccess.Open();
            using var cmd = new SqliteCommand("SELECT Key, Value FROM Settings", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                dict[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            _cache = dict;
            return dict;
        }
    }

    /// <summary>使设置缓存失效（下次 Get 重新全量载入）。仅供底层直写库的兜底场景调用。</summary>
    public static void InvalidateCache()
    {
        lock (_cacheLock) _cache = null;
    }

    /// <summary>
    /// 按 Key 获取单个设置值（内存缓存命中，无库访问）
    /// </summary>
    /// <param name="key">设置项键名</param>
    /// <param name="defaultValue">未找到时的默认返回值</param>
    /// <returns>设置值字符串，未找到则返回 defaultValue</returns>
    public static string? Get(string key, string? defaultValue = null)
    {
        // 初始化检查：保证 Settings 表已存在
        EnsureInit();
        // 缓存命中即返回；键存在但值为 NULL 时同样回退 defaultValue（与旧实现语义一致）
        var cache = LoadCache();
        lock (_cacheLock)
        {
            return cache.TryGetValue(key, out var value) ? (value ?? defaultValue) : defaultValue;
        }
    }

    /// <summary>
    /// 设置某个配置项的值（存在则更新，不存在则插入），并同步内存缓存
    /// </summary>
    /// <param name="key">设置项键名</param>
    /// <param name="value">设置值</param>
    public static void Set(string key, string value)
    {
        // 初始化检查：保证 Settings 表已存在
        EnsureInit();
        // UPSERT：Key 是 UNIQUE 的，冲突时更新 Value
        // 单语句原子完成“有则改、无则插”，避免先查后写的竞态
        const string sql = @"
            INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = @Value";

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 创建写入命令
        using var cmd = new SqliteCommand(sql, conn);
        // 绑定键名参数
        cmd.Parameters.AddWithValue("@Key", key);
        // 绑定新值参数
        cmd.Parameters.AddWithValue("@Value", value);
        // 执行 UPSERT 写入
        cmd.ExecuteNonQuery();
        // 同步内存缓存
        lock (_cacheLock)
        {
            var cache = LoadCache();
            cache[key] = value;
        }
    }

    /// <summary>
    /// 批量写入设置（单连接+单事务），并同步内存缓存
    /// 2026-08-23：设置页保存要写 20+ 个键，逐键 Set 会产生同等次数的连接/命令开销，
    /// 造成保存瞬间卡顿；合并为一次事务后显著加快。
    /// </summary>
    /// <param name="items">要写入的键值对集合</param>
    public static void SetMany(IEnumerable<KeyValuePair<string, string>> items)
    {
        // 初始化检查：保证 Settings 表已存在
        EnsureInit();
        // 与 Set 相同的 UPSERT 语句，但命令只建一次、参数反复复用
        const string sql = @"
            INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = @Value";

        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 开启事务：全部键一次性提交，失败整体回滚
        using var tx = conn.BeginTransaction();
        // 创建共享命令并挂到事务上
        using var cmd = new SqliteCommand(sql, conn, tx);
        // 预先添加参数占位（循环内仅改 Value，避免每轮重建参数对象）
        var pKey = cmd.Parameters.Add("@Key", SqliteType.Text);
        // 同上，Value 参数
        var pVal = cmd.Parameters.Add("@Value", SqliteType.Text);
        // 遍历所有待写键值对
        foreach (var kv in items)
        {
            // 仅替换参数值（不重新 Add），这是批量写入的标准优化
            pKey.Value = kv.Key;
            // null 值归一为数据库 NULL
            pVal.Value = (object?)kv.Value ?? DBNull.Value;
            // 执行当前键的 UPSERT
            cmd.ExecuteNonQuery();
        }
        // 全部成功后一次性提交
        tx.Commit();
        // 同步内存缓存
        lock (_cacheLock)
        {
            var cache = LoadCache();
            foreach (var kv in items)
                cache[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// 获取全部设置项
    /// </summary>
    /// <returns>字典：键 → 值</returns>
    public static Dictionary<string, string> GetAll()
    {
        // 初始化检查：保证 Settings 表已存在
        EnsureInit();
        // 结果字典：键 → 值
        var dict = new Dictionary<string, string>();
        // 打开就绪连接（内部含初始化检查）
        using var conn = DbAccess.Open();
        // 查全部设置项，不做过滤
        using var cmd = new SqliteCommand("SELECT Key, Value FROM Settings", conn);
        // 执行全表扫描（Settings 行数有限，可接受）
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // 第0列=键；第1列=值，NULL 归一为空串（注意与 Get 的 null 语义不同）
            dict[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        // 返回完整设置快照
        return dict;
    }

    /// <summary>
    /// 默认设置值——单一数据源，DatabaseHelper.Initialize 和 BtnRestoreDefault 共用
    /// </summary>
    public static readonly Dictionary<string, string> Defaults = new()
    {
        // 采集相关
        ["PollIntervalSeconds"] = "3",              // 轮询间隔（秒）
        ["IdleThresholdSeconds"] = "300",           // 空闲判定阈值（秒），5分钟无操作算空闲
        ["AutoStartTracking"] = "true",             // 启动后自动开始追踪
        // 截图相关
        ["EnableScreenshot"] = "false",             // 是否开启截图功能
        ["ScreenshotOnSwitch"] = "true",            // 切换窗口时截图
        ["ScreenshotIntervalMinutes"] = "5",        // 定时截图间隔（分钟）
        ["ScreenshotFormat"] = "jpg",               // 截图格式
        ["ScreenshotPath"] = "",                    // 截图保存路径（空=默认路径）
        ["ScreenshotQuality"] = "medium",           // 截图质量
        ["EnableMaxSize"] = "true",                 // 启用截图存储上限
        ["MaxScreenshotSizeMB"] = "5120",           // 截图最大占用空间（MB），5GB
        ["EnableMaxAge"] = "true",                  // 启用截图过期清理
        ["MaxScreenshotAgeDays"] = "30",            // 截图保留天数
        // 外观相关
        ["ColorScheme"] = "default",                // 颜色方案
        ["Theme"] = "light",                        // 主题
        // 数据相关
        ["DataRetentionDays"] = "90",               // 数据保留天数
        // AI 相关
["EnableAI"] = "true",                      // 是否启用 AI 总结
["AIProvider"] = "custom",                  // 服务商预设：默认"自定义"（不预填任何本地服务）
["AIApiUrl"] = "",                          // AI API 地址（由用户按所选服务商填写）
["AIApiKey"] = "",                          // AI API Key
["AIModel"] = "",                           // AI 模型名称
        ["AISummaryPath"] = "",                    // AI 总结保存路径
        ["AISummaryMaxCount"] = "30",              // AI 总结最大保留条数
        ["AISummaryMaxSizeMB"] = "50",             // AI 总结最大占用空间（MB）
        ["AutoDailySummary"] = "true",             // 每日 0:00 自动生成每日总结
        ["AutoWeeklySummary"] = "true",            // 每周一 0:00 自动生成每周总结
        ["AutoMonthlySummary"] = "true",          // 每月 1 号 0:00 自动生成每月总结
        // 系统相关
        ["AutoStartWithWindows"] = "false",        // 开机自启
        ["MinimizeToTray"] = "true",               // 关闭时最小化到托盘
    };

    /// <summary>
    /// 按设置页分组返回默认值（BtnRestoreDefault 用，每个页签只恢复对应设置项）
    /// </summary>
    /// <param name="navIndex">页签索引：0=常规，1=截图，4=数据，5=AI，6=系统</param>
    /// <returns>该页对应的默认设置字典</returns>
    public static Dictionary<string, string> GetDefaultsByPage(int navIndex) => navIndex switch
    {
        // 常规页：采集行为三项
        0 => FilterDefaults("PollIntervalSeconds", "IdleThresholdSeconds", "AutoStartTracking"),
        // 截图页：开关/触发方式/间隔/格式/路径/质量 + 容量与过期清理上限
        1 => FilterDefaults("EnableScreenshot", "ScreenshotOnSwitch", "ScreenshotIntervalMinutes",
            "ScreenshotFormat", "ScreenshotPath", "ScreenshotQuality",
            "EnableMaxSize", "MaxScreenshotSizeMB", "EnableMaxAge", "MaxScreenshotAgeDays"),
        // 数据页：保留天数
        4 => FilterDefaults("DataRetentionDays"),
        // AI 页：启用/服务商/地址/密钥/模型 + 总结文件路径与容量上限 + 三种自动总结开关
        5 => FilterDefaults("EnableAI", "AIProvider", "AIApiUrl", "AIApiKey", "AIModel",
            "AISummaryPath", "AISummaryMaxCount", "AISummaryMaxSizeMB",
            "AutoDailySummary", "AutoWeeklySummary", "AutoMonthlySummary"),
        // 系统页：自启与托盘行为
        6 => FilterDefaults("AutoStartWithWindows", "MinimizeToTray"),
        // 其余页签（2/3 等）：无对应默认项，返回空字典
        _ => new()
    };

    /// <summary>
    /// 从 Defaults 中筛选指定的 key 返回子字典
    /// </summary>
    /// <param name="keys">要筛选的键名数组</param>
    /// <returns>只包含指定键的字典</returns>
    private static Dictionary<string, string> FilterDefaults(params string[] keys)
    {
        // 子字典容器
        var result = new Dictionary<string, string>();
        // 遍历请求的键名
        foreach (var key in keys)
            // 只收录 Defaults 中真实存在的键（防拼写错误导致 KeyError）
            if (Defaults.TryGetValue(key, out var val))
                result[key] = val;
        // 返回筛选后的子集
        return result;
    }
}
