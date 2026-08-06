using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TimeActivity.Services;

namespace TimeActivity.Data;

/// <summary>
/// 数据库基础设施 — 负责建库建表、连接管理、备份、数据清理
/// 各表的 CRUD 操作请用对应的 Repository 类
/// </summary>
public class DatabaseHelper
{
    private static readonly string DbPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");

    public static string ConnectionString => $"Data Source={DbPath}";

    private static bool _initialized = false;

    /// <summary>
    /// 初始化数据库 — 首次运行时自动建表 + 插初始数据
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            // 开启 WAL 模式提升并发性能
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragmaCmd.ExecuteNonQuery();

            Logger.Info("数据库初始化：WAL 已开启");

            // 建表
            var sql = @"
            CREATE TABLE IF NOT EXISTS Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Color TEXT NOT NULL DEFAULT '#808080',
                Icon TEXT NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Activities (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '未分类',
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                Duration INTEGER NOT NULL DEFAULT 0,
                IsIdle INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS Rules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                TitleKeyword TEXT,
                CategoryId INTEGER NOT NULL,
                IsCustom INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
            );

            CREATE TABLE IF NOT EXISTS Screenshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath TEXT NOT NULL,
                CapturedAt TEXT NOT NULL,
                FileSize INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS DailyTotal (
                Date TEXT NOT NULL PRIMARY KEY,
                TotalActiveSeconds INTEGER NOT NULL DEFAULT 0,
                TotalSeconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS DailyCategorySummary (
                Date TEXT NOT NULL,
                Category TEXT NOT NULL,
                Seconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (Date, Category)
            );

            CREATE TABLE IF NOT EXISTS DailyProcessSummary (
                Date TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '未分类',
                Seconds INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (Date, ProcessName)
            );

            CREATE TABLE IF NOT EXISTS AISummaries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL,
                SummaryText TEXT NOT NULL,
                SummaryType TEXT NOT NULL DEFAULT 'daily',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL UNIQUE,
                Value TEXT
            );

            CREATE TABLE IF NOT EXISTS AppColors (
                ProcessName TEXT NOT NULL PRIMARY KEY,
                Color TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE INDEX IF NOT EXISTS IX_Activities_StartTime ON Activities(StartTime);
            CREATE INDEX IF NOT EXISTS IX_Activities_Category ON Activities(Category);
            CREATE INDEX IF NOT EXISTS IX_Activities_ProcessName ON Activities(ProcessName);
            CREATE INDEX IF NOT EXISTS IX_Screenshots_CapturedAt ON Screenshots(CapturedAt);
            CREATE INDEX IF NOT EXISTS IX_DailyCategorySummary_Date ON DailyCategorySummary(Date);
            CREATE INDEX IF NOT EXISTS IX_DailyProcessSummary_Date ON DailyProcessSummary(Date);
        ";

            using var createCmd = new SqliteCommand(sql, conn);
            createCmd.ExecuteNonQuery();

            // 迁移：检查 AISummaries 是否有 AutoType 列，没有就加
            try
            {
                using var checkCol = new SqliteCommand("PRAGMA table_info(AISummaries)", conn);
                using var reader = checkCol.ExecuteReader();
                bool hasAutoType = false;
                while (reader.Read())
                {
                    if (reader.GetString(1) == "AutoType") { hasAutoType = true; break; }
                }
                if (!hasAutoType)
                {
                    using var alterCmd = new SqliteCommand("ALTER TABLE AISummaries ADD COLUMN AutoType TEXT NOT NULL DEFAULT 'manual'", conn);
                    alterCmd.ExecuteNonQuery();
                    Logger.Info("数据库迁移：AISummaries 表已加 AutoType 字段");
                }
            }
            catch { }

            // 创建唯一索引（如果不存在）— 用于 UPSERT
            try
            {
                using var idxCmd = new SqliteCommand("CREATE UNIQUE INDEX IF NOT EXISTS UX_AISummaries_Type ON AISummaries(Date, SummaryType, AutoType)", conn);
                idxCmd.ExecuteNonQuery();
            }
            catch { }

            // 插入预置分类（如果还没有）
            var countCmd = new SqliteCommand("SELECT COUNT(*) FROM Categories", conn);
            if ((long)countCmd.ExecuteScalar()! == 0)
            {
                var cats = new[]
                {
                    ("开发工具", "#4A90D9", "code", 1),
                    ("社交通讯", "#E67E22", "chat", 2),
                    ("游戏", "#E74C3C", "gamepad", 3),
                    ("办公学习", "#2ECC71", "book", 4),
                    ("浏览器", "#9B59B6", "globe", 5),
                    ("视频娱乐", "#FF6B6B", "video", 6),
                    ("音乐", "#AB47BC", "music", 7),
                    ("设计创作", "#FFA726", "palette", 8),
                    ("实用工具", "#26C6DA", "wrench", 9),
                    ("AI助手", "#EC407A", "robot", 10),
                    ("系统组件", "#7CB9E8", "desktop", 11),
                    ("空闲", "#CFD8DC", "coffee", 12),
                    ("未分类", "#90A4AE", "question", 13),
                };
                foreach (var (name, color, icon, order) in cats)
                {
                    var insertCat = new SqliteCommand(
                        "INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES (@Name, @Color, @Icon, @SortOrder)", conn);
                    insertCat.Parameters.AddWithValue("@Name", name);
                    insertCat.Parameters.AddWithValue("@Color", color);
                    insertCat.Parameters.AddWithValue("@Icon", icon);
                    insertCat.Parameters.AddWithValue("@SortOrder", order);
                    insertCat.ExecuteNonQuery();
                }
            }

            // 插入预置设置项（如果还没有）
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Settings", conn);
            if ((long)countCmd.ExecuteScalar()! == 0)
            {
                var settings = new[]
                {
                    ("PollIntervalSeconds", "3"),
                    ("IdleThresholdSeconds", "300"),
                    ("AutoStartTracking", "true"),
                    ("EnableScreenshot", "false"),
                    ("ScreenshotIntervalMinutes", "5"),
                    ("ScreenshotPath", ""),
                    ("ScreenshotQuality", "medium"),
                    ("ColorScheme", "default"),
                    ("Theme", "light"),
                    ("DataRetentionDays", "90"),
                    ("EnableAI", "true"),
                    ("AIApiUrl", "https://api.minimax.chat/v1/text/chatcompletion_v2"),
                    ("AIApiKey", ""),
                    ("AutoDailySummary", "true"),
                    ("AutoStartWithWindows", "true"),
                    ("MinimizeToTray", "true"),
                };
                foreach (var (key, value) in settings)
                {
                    var insertSet = new SqliteCommand(
                        "INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)", conn);
                    insertSet.Parameters.AddWithValue("@Key", key);
                    insertSet.Parameters.AddWithValue("@Value", value);
                    insertSet.ExecuteNonQuery();
                }
            }

            // 插入预置分类规则（如果 Rules 表为空）
            countCmd = new SqliteCommand("SELECT COUNT(*) FROM Rules", conn);
            if ((long)countCmd.ExecuteScalar()! == 0)
            {
                // 查分类名→ID 映射
                var catMap = new Dictionary<string, int>();
                using (var catQ = new SqliteCommand("SELECT Id, Name FROM Categories", conn))
                using (var catR = catQ.ExecuteReader())
                    while (catR.Read())
                        catMap[catR.GetString(1)] = catR.GetInt32(0);

                // 预置进程规则（IsCustom=0 表示预置不可删，全部进程名精确匹配）
                // 完整列表见 Z:\final\相关方案\26-8-05-23-初始规则数据完整版.md
                var procRules = new[]
                {
                    ("devenv", "开发工具"), ("code", "开发工具"), ("code-insiders", "开发工具"),
                    ("idea64", "开发工具"), ("idea", "开发工具"), ("pycharm64", "开发工具"),
                    ("pycharm", "开发工具"), ("webstorm64", "开发工具"), ("rider64", "开发工具"),
                    ("clion64", "开发工具"), ("goland64", "开发工具"), ("phpstorm64", "开发工具"),
                    ("rubymine64", "开发工具"), ("studio64", "开发工具"), ("cmd", "开发工具"),
                    ("powershell", "开发工具"), ("pwsh", "开发工具"), ("WindowsTerminal", "开发工具"),
                    ("alacritty", "开发工具"), ("mintty", "开发工具"), ("git", "开发工具"),
                    ("github", "开发工具"), ("sourcetree", "开发工具"), ("gitkraken", "开发工具"),
                    ("node", "开发工具"), ("npm", "开发工具"), ("python", "开发工具"),
                    ("pythonw", "开发工具"), ("dotnet", "开发工具"), ("java", "开发工具"),
                    ("javaw", "开发工具"), ("go", "开发工具"), ("cargo", "开发工具"),
                    ("mvn", "开发工具"), ("gradle", "开发工具"), ("cmake", "开发工具"),
                    ("make", "开发工具"), ("navicat", "开发工具"), ("ssms", "开发工具"),
                    ("pgadmin4", "开发工具"), ("dbeaver", "开发工具"), ("mongodbcompass", "开发工具"),
                    ("apifox", "开发工具"), ("postman", "开发工具"), ("insomnia", "开发工具"),
                    ("filezilla", "开发工具"), ("putty", "开发工具"), ("xshell", "开发工具"),
                    ("mobaxterm", "开发工具"), ("winscp", "开发工具"), ("vmware", "开发工具"),
                    ("virtualbox", "开发工具"), ("qemu-system-x86_64", "开发工具"),
                    ("docker", "开发工具"), ("draw.io", "开发工具"), ("ilspy", "开发工具"),
                    ("dotPeek", "开发工具"), ("beyondeditor", "开发工具"), ("qtscrcpy", "开发工具"),
                    ("electron", "开发工具"), ("vim", "开发工具"), ("gvim", "开发工具"),
                    ("neovim", "开发工具"), ("sublime_text", "开发工具"), ("notepad++", "开发工具"),
                    ("wireshark", "开发工具"), ("fiddler", "开发工具"), ("processhacker", "开发工具"),
                    ("procmon", "开发工具"), ("procexp", "开发工具"), ("x64dbg", "开发工具"),
                    ("x32dbg", "开发工具"), ("ida", "开发工具"), ("ghidra", "开发工具"),
                    ("winhex", "开发工具"), ("winhex64", "开发工具"), ("imhex-gui", "开发工具"),
                    ("copilotnative", "开发工具"), ("cursor", "开发工具"), ("windsurf", "开发工具"),
                    ("zed", "开发工具"), ("fleet", "开发工具"), ("rstudio", "开发工具"),
                    ("matlab", "开发工具"), ("mathematica", "开发工具"), ("jupyter", "开发工具"),
                    ("spyder", "开发工具"), ("arduino", "开发工具"), ("platformio", "开发工具"),
                    ("keil", "开发工具"), ("stm32cubeide", "开发工具"),
                    ("chrome", "浏览器"), ("msedge", "浏览器"), ("firefox", "浏览器"),
                    ("brave", "浏览器"), ("opera", "浏览器"), ("vivaldi", "浏览器"),
                    ("arc", "浏览器"), ("maxthon", "浏览器"), ("360chrome", "浏览器"),
                    ("quark", "浏览器"), ("tor-browser", "浏览器"), ("chromium", "浏览器"),
                    ("YuanShen", "游戏"), ("genshinimpact", "游戏"),
                    ("starrail", "游戏"), ("hyp", "游戏"), ("hypergryph", "游戏"),
                    ("deltaforceclient-win64-shipping", "游戏"), ("delta_force_launcher", "游戏"),
                    ("deltaforcesingle-win64-shipping", "游戏"), ("nzmclient", "游戏"),
                    ("nzm_launcher", "游戏"), ("BetterGI", "游戏"),
                    ("Plain Craft Launcher 2", "游戏"), ("minecraft", "游戏"),
                    ("minecraft.windows", "游戏"), ("steam", "游戏"), ("steamwebhelper", "游戏"),
                    ("wegame", "游戏"), ("epicgameslauncher", "游戏"), ("riotclientservices", "游戏"),
                    ("leagueclient", "游戏"), ("VALORANT-Win64-Shipping", "游戏"),
                    ("battlenet", "游戏"), ("origin", "游戏"), ("upc", "游戏"),
                    ("gta5", "游戏"), ("terraria", "游戏"), ("portal2", "游戏"),
                    ("csgo", "游戏"), ("cs2", "游戏"), ("dota2", "游戏"),
                    ("guilongchao", "游戏"), ("startgame", "游戏"), ("misidefull", "游戏"),
                    ("hogwartslegacy", "游戏"), ("daysgone", "游戏"), ("endfield", "游戏"),
                    ("honeyselect2", "游戏"), ("ai-syoujyo", "游戏"), ("doax_vv", "游戏"),
                    ("atelierresleriana", "游戏"), ("eldenring", "游戏"),
                    ("cyberpunk2077", "游戏"), ("witcher3", "游戏"), ("rdr2", "游戏"),
                    ("skyrim", "游戏"), ("fallout4", "游戏"), ("roblox", "游戏"),
                    ("fortnite", "游戏"), ("pubg", "游戏"), ("apex_legends", "游戏"),
                    ("overwatch", "游戏"), ("lostlight", "游戏"), ("fs2024", "游戏"),
                    ("starcitizen", "游戏"), ("wows", "游戏"), ("wt", "游戏"),
                    ("game", "游戏"),
                    ("WeChat", "社交通讯"), ("wechatappex", "社交通讯"),
                    ("QQ", "社交通讯"), ("dingtalk", "社交通讯"),
                    ("telegram", "社交通讯"), ("discord", "社交通讯"), ("slack", "社交通讯"),
                    ("teams", "社交通讯"), ("feishu", "社交通讯"), ("lark", "社交通讯"),
                    ("skype", "社交通讯"), ("zoom", "社交通讯"), ("line", "社交通讯"),
                    ("whatsapp", "社交通讯"), ("kook", "社交通讯"), ("yy", "社交通讯"),
                    ("teamviewer", "社交通讯"), ("anydesk", "社交通讯"), ("todesk", "社交通讯"),
                    ("asklink", "社交通讯"), ("sunlogin", "社交通讯"), ("awesun", "社交通讯"),
                    ("qqlive", "视频娱乐"), ("douyin", "视频娱乐"), ("哔哩哔哩", "视频娱乐"),
                    ("bilibili", "视频娱乐"), ("livehime", "视频娱乐"), ("obs64", "视频娱乐"),
                    ("jianyingpro", "视频娱乐"), ("tencentvideo", "视频娱乐"), ("iqiyi", "视频娱乐"),
                    ("youku", "视频娱乐"), ("mgtv", "视频娱乐"), ("netflix", "视频娱乐"),
                    ("youtube", "视频娱乐"), ("twitch", "视频娱乐"), ("potplayer", "视频娱乐"),
                    ("kmplayer", "视频娱乐"), ("gomplayer", "视频娱乐"), ("vlc", "视频娱乐"),
                    ("mpc-hc", "视频娱乐"), ("mpc-be", "视频娱乐"), ("handbrake", "视频娱乐"),
                    ("losslesscut", "视频娱乐"), ("shotcut", "视频娱乐"),
                    ("davinciresolve", "视频娱乐"), ("premiere", "视频娱乐"), ("afterfx", "视频娱乐"),
                    ("QQMusic", "音乐"), ("spotify", "音乐"),
                    ("musicplayer2", "音乐"), ("cloudmusic", "音乐"),
                    ("netease_cloudmusic", "音乐"), ("kugou", "音乐"), ("kuwo", "音乐"),
                    ("foobar2000", "音乐"), ("aimp", "音乐"), ("mpv", "音乐"),
                    ("AppleMusic", "音乐"), ("ytmdesktop", "音乐"), ("algermusicplayer", "音乐"),
                    ("mediamonkey", "音乐"), ("musicbee", "音乐"),
                    ("WINWORD", "办公学习"), ("EXCEL", "办公学习"),
                    ("POWERPNT", "办公学习"), ("ONENOTE", "办公学习"),
                    ("OUTLOOK", "办公学习"), ("pdfeditor", "办公学习"),
                    ("acrobat", "办公学习"), ("sumatrapdf", "办公学习"), ("foxit", "办公学习"),
                    ("pdfxedit", "办公学习"), ("xmind", "办公学习"), ("typora", "办公学习"),
                    ("notion", "办公学习"), ("obsidian", "办公学习"), ("evernote", "办公学习"),
                    ("wiznote", "办公学习"), ("logseq", "办公学习"), ("anki", "办公学习"),
                    ("cxstudy", "办公学习"), ("packettracer", "办公学习"),
                    ("multisim", "办公学习"), ("mathtype", "办公学习"),
                    ("geogebra", "办公学习"), ("zotero", "办公学习"),
                    ("endnote", "办公学习"), ("mindmanager", "办公学习"), ("visio", "办公学习"),
                    ("photoshop", "设计创作"), ("illustrator", "设计创作"),
                    ("indesign", "设计创作"), ("audition", "设计创作"),
                    ("lightroom", "设计创作"), ("bridge", "设计创作"),
                    ("blender", "设计创作"), ("3dsmax", "设计创作"), ("maya", "设计创作"),
                    ("cinema4d", "设计创作"), ("c4d", "设计创作"), ("autocad", "设计创作"),
                    ("sketchup", "设计创作"), ("figma", "设计创作"), ("mastergo", "设计创作"),
                    ("canva", "设计创作"), ("coreldraw", "设计创作"), ("gimp", "设计创作"),
                    ("inkscape", "设计创作"), ("affinity", "设计创作"), ("sai", "设计创作"),
                    ("krita", "设计创作"), ("procreate", "设计创作"),
                    ("sketch", "设计创作"), ("ptgui", "设计创作"), ("edrawmax", "设计创作"),
                    ("axure", "设计创作"), ("即时设计", "设计创作"),
                    ("explorer", "系统组件"), ("Taskmgr", "系统组件"),
                    ("SystemSettings", "系统组件"), ("SearchHost", "系统组件"),
                    ("ShellExperienceHost", "系统组件"), ("StartMenuExperienceHost", "系统组件"),
                    ("ApplicationFrameHost", "系统组件"), ("CredentialUIBroker", "系统组件"),
                    ("HideVolumeOSD", "系统组件"), ("dwm", "系统组件"), ("sihost", "系统组件"),
                    ("lockapp", "系统组件"), ("regedit", "系统组件"), ("mmc", "系统组件"),
                    ("systempropertiesadvanced", "系统组件"),
                    ("systempropertiescomputername", "系统组件"),
                    ("systempropertiesperformance", "系统组件"),
                    ("pickerhost", "系统组件"), ("runtimebroker", "系统组件"),
                    ("csrss", "系统组件"), ("fontdrvhost", "系统组件"),
                    ("logonui", "系统组件"), ("svchost", "系统组件"),
                    ("spoolsv", "系统组件"), ("services", "系统组件"),
                    ("compmgmt", "系统组件"), ("devmgmt", "系统组件"),
                    ("mblctr", "系统组件"), ("perfmon", "系统组件"),
                    ("msinfo32", "系统组件"), ("dxdiag", "系统组件"),
                    ("optionalfeatures", "系统组件"), ("netplwiz", "系统组件"),
                    ("shutdown", "系统组件"), ("winlogon", "系统组件"),
                    ("textinputhost", "系统组件"),
                    ("snipaste", "实用工具"), ("Notepad", "实用工具"),
                    ("Photos", "实用工具"), ("Thunder", "实用工具"),
                    ("everything", "实用工具"), ("7zfm", "实用工具"), ("7zg", "实用工具"),
                    ("WinRAR", "实用工具"), ("bandizip", "实用工具"),
                    ("idman", "实用工具"), ("calculatorapp", "实用工具"), ("calc", "实用工具"),
                    ("wiztree64", "实用工具"), ("wiztree", "实用工具"),
                    ("geek64", "实用工具"), ("geek", "实用工具"),
                    ("aida64", "实用工具"), ("cpuz", "实用工具"), ("gpuz", "实用工具"),
                    ("nlclientapp", "实用工具"), ("mspaint", "实用工具"),
                    ("paintstudio", "实用工具"), ("onethingpclite", "实用工具"),
                    ("123pan", "实用工具"), ("baidunetdisk", "实用工具"),
                    ("baidunetdiskunite", "实用工具"), ("quarkclouddrive", "实用工具"),
                    ("aliyundrive", "实用工具"), ("aliyunpan", "实用工具"),
                    ("onedrive", "实用工具"), ("dropbox", "实用工具"),
                    ("googledrive", "实用工具"), ("listary", "实用工具"),
                    ("totalcmd", "实用工具"), ("files", "实用工具"),
                    ("renamer", "实用工具"), ("unlocker", "实用工具"),
                    ("poweriso", "实用工具"), ("ultraiso", "实用工具"),
                    ("rufus", "实用工具"), ("ventoy", "实用工具"),
                    ("etcher", "实用工具"), ("diskgenius", "实用工具"),
                    ("partitionwizard", "实用工具"), ("translucenttb", "实用工具"),
                    ("volume2", "实用工具"), ("powertoys", "实用工具"),
                    ("autohotkey", "实用工具"), ("autohotkeyu64", "实用工具"),
                    ("altsnap", "实用工具"), ("contextmenumanager", "实用工具"),
                    ("QClaw", "AI助手"), ("doubao", "AI助手"),
                    ("ollama app", "AI助手"), ("ollama", "AI助手"),
                    ("lm studio", "AI助手"), ("chatgpt", "AI助手"),
                    ("ima.copilot", "AI助手"), ("copilot", "AI助手"),
                    ("claude", "AI助手"), ("gemini", "AI助手"),
                    ("poe", "AI助手"), ("perplexity", "AI助手"),
                };
                foreach (var (proc, cat) in procRules)
                {
                    if (catMap.TryGetValue(cat, out int catId))
                    {
                        using var ins = new SqliteCommand(
                            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@p, NULL, @c, 0)", conn);
                        ins.Parameters.AddWithValue("@p", proc);
                        ins.Parameters.AddWithValue("@c", catId);
                        ins.ExecuteNonQuery();
                    }
                }

                Logger.Info($"预置 {procRules.Length} 条分类规则已写入 Rules 表");
            }

            _initialized = true;
            Logger.Info("数据库初始化完成");
        }
        catch (Exception ex)
        {
            Logger.Error("数据库初始化失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 测试数据库连接（同时初始化）
    /// </summary>
    public static bool TestConnection()
    {
        try
        {
            Initialize();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 备份数据库到指定路径（VACUUM INTO，不需要停引擎）
    /// </summary>
    public static void BackupTo(string targetPath)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{targetPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
        Logger.Info($"数据库备份到 {targetPath}");
    }

    /// <summary>
    /// 清空所有数据（保留设置和分类）
    /// </summary>
    public static void ClearAllData()
    {
        Initialize();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        string[] tables = { "Activities", "Screenshots", "DailyTotal", "DailyCategorySummary", "DailyProcessSummary", "AISummaries" };
        foreach (var table in tables)
        {
            using var cmd = new SqliteCommand($"DELETE FROM {table}", conn);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 重新分类所有历史活动记录（规则更新后调用）
    /// </summary>
    public static int ReclassifyAll(System.Func<string, string, string> classifyFunc)
    {
        Initialize();
        int updated = 0;
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 取所有非空闲活动记录
        using var selCmd = new SqliteCommand(
            "SELECT Id, ProcessName, WindowTitle FROM Activities WHERE IsIdle=0", conn);
        using var reader = selCmd.ExecuteReader();

        var updates = new List<(long id, string category)>();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string proc = reader.GetString(1);
            string title = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string newCat = classifyFunc(proc, title);
            updates.Add((id, newCat));
        }

        reader.Close();

        foreach (var (id, cat) in updates)
        {
            using var updCmd = new SqliteCommand(
                "UPDATE Activities SET Category=@c WHERE Id=@id", conn);
            updCmd.Parameters.AddWithValue("@c", cat);
            updCmd.Parameters.AddWithValue("@id", id);
            updated += updCmd.ExecuteNonQuery();
        }

        // 重新生成每日汇总
        using var datesCmd = new SqliteCommand(
            "SELECT DISTINCT date(StartTime) FROM Activities", conn);
        using var dateReader = datesCmd.ExecuteReader();
        var dates = new List<string>();
        while (dateReader.Read())
            dates.Add(dateReader.GetString(0));
        dateReader.Close();

        foreach (var date in dates)
            DailySummaryRepository.GenerateForDate(date);

        if (updated > 0)
            Logger.Info($"重新分类完成：更新 {updated} 条活动记录，重新生成 {dates.Count} 天汇总");

        return updated;
    }

    /// <summary>
    /// 清理超过指定天数的旧数据
    /// </summary>
    public static int CleanOldData(int retentionDays)
    {
        Initialize();
        string cutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        string dateCutoff = DateTime.Now.AddDays(-retentionDays).ToString("yyyy-MM-dd");
        int totalDeleted = 0;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // 1. 清 Activities
        using var cmd1 = new SqliteCommand("DELETE FROM Activities WHERE StartTime < @Cutoff", conn);
        cmd1.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd1.ExecuteNonQuery();

        // 2. 清 Screenshots（同时删文件）
        using var cmd2 = new SqliteCommand("SELECT FilePath FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd2.Parameters.AddWithValue("@Cutoff", cutoff);
        using (var reader = cmd2.ExecuteReader())
        {
            while (reader.Read())
            {
                try
                {
                    var p = reader.GetString(0);
                    string fullPath = Path.IsPathRooted(p) ? p : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }
                catch { }
            }
        }
        using var cmd3 = new SqliteCommand("DELETE FROM Screenshots WHERE CapturedAt < @Cutoff", conn);
        cmd3.Parameters.AddWithValue("@Cutoff", cutoff);
        totalDeleted += cmd3.ExecuteNonQuery();

        // 3. 清 AISummaries（手动总结超期也删，自动总结永久保留）
        using var cmd4 = new SqliteCommand("DELETE FROM AISummaries WHERE Date < @DateCutoff AND AutoType='manual'", conn);
        cmd4.Parameters.AddWithValue("@DateCutoff", dateCutoff);
        totalDeleted += cmd4.ExecuteNonQuery();

        // 4. 清每日汇总三张表
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
