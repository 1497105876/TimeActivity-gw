import sqlite3
import sys
import json
sys.stdout.reconfigure(encoding='utf-8')

# === 读取两个数据库 ===

# TimeActivity
conn1 = sqlite3.connect(r'Z:\final\code\TimeActivity\bin\Debug\net8.0-windows\timeactivity.db')
c1 = conn1.cursor()
c1.execute("SELECT DISTINCT ProcessName FROM Activities WHERE ProcessName != '(空闲)' ORDER BY ProcessName")
ta_procs = set(r[0] for r in c1.fetchall())
conn1.close()

# ManicTime
conn2 = sqlite3.connect(r'C:\Users\highness\AppData\Local\Finkit\ManicTime\ManicTimeReports.db')
c2 = conn2.cursor()
c2.execute("SELECT Key, Name FROM Ar_CommonGroup WHERE ReportGroupType=1")
mt_apps = []
for key, name in c2.fetchall():
    proc = ""
    if key and ';' in key:
        proc = key.split(';')[0].lower()
    elif key:
        proc = key.lower()
    if proc and proc != "mmc.exe":  # mmc 太泛了跳过
        mt_apps.append((proc, name or proc))
conn2.close()

# 合并所有进程名
all_procs = set()
all_procs.update(ta_procs)
all_procs.update(proc for proc, _ in mt_apps)

# 去掉明显的临时文件/安装包
def is_temp(proc):
    proc_lower = proc.lower()
    if proc_lower.endswith('.tmp'):
        return True
    if 'setup' in proc_lower or 'installer' in proc_lower or 'uninstall' in proc_lower:
        return True
    if proc_lower.startswith('_') or proc_lower.startswith('01-') or proc_lower.startswith('a8'):
        # 临时文件特征
        if proc_lower.endswith('.tmp') or proc_lower.endswith('.exe.tmp'):
            return True
    # 纯数字开头的临时文件
    if proc_lower[0:1].isdigit() and len(proc_lower) > 20:
        return True
    return False

all_procs = {p for p in all_procs if not is_temp(p)}

print(f"TimeActivity 进程数: {len(ta_procs)}")
print(f"ManicTime 进程数: {len(set(proc for proc, _ in mt_apps))}")
print(f"合并去重后: {len(all_procs)}")

# === 分类规则 ===
# 基于现有8分类 + 可能新增的分类
categories = {
    "开发": [],
    "社交": [],
    "娱乐": [],
    "学习": [],
    "系统": [],
    "网页": [],
    "工具": [],   # 新增：实用工具
    "游戏": [],   # 新增：游戏
    "设计": [],   # 新增：设计/创意
    "未分类": [],
}

# 分类关键词映射
rules = {
    "开发": [
        "devenv", "code", "idea64", "pycharm64", "webstorm64", "rider64", "clion64",
        "goland64", "phpstorm64", "rubymine64", "android", "studio64",
        "cmd", "powershell", "windowsterminal", "pwsh", "git", "github",
        "node", "npm", "python", "pythonw", "py", "dotnet", "msbuild",
        "java", "javaw", "eclipse", "netbeans", "vim", "gvim", "neovim",
        "sublime_text", "notepad++", "notepadpp",
        "docker", "wsl", "vmware", "virtualbox",
        "navicat", "ssms", "sql", "heidisql", "dbeaver", "mysql",
        "postman", "apifox", "insomnia",
        "filezilla", "winscp", "putty", "xshell", "mobaxterm",
        "tortoisesvn", "svn", "kdiff3", "beyondcompare", "winmerge",
        "visualparadigm", "staruml", "drawio",
        "gradle", "maven", "ant",
        "cmake", "make", "ninja",
        "rust-rover", "rustc", "cargo",
        "go", "golang",
        "vim", "emacs",
        "ssh",
        "qclaw", "ollama",
    ],
    "社交": [
        "wechat", "qq", "tim", "discord", "telegram", "slack",
        "dingtalk", "wecom", "wxwork",
        "skype", "teams", "zoom", "feishu", "lark",
        "gmail", "outlook", "foxmail", "thunderbird",
        "message", "imessage",
        "line", "whatsapp",
        "kook", "yy", "ktv",
    ],
    "娱乐": [
        # 音乐
        "spotify", "qqmusic", "musicplayer2", "cloudmusic", "netease",
        "foobar2000", "aimp", "potplayer", "vlc", "mpv", "mpc-hc",
        "kmplayer", "gomplayer",
        # 视频
        "bilibili", "youtube",
        # 直播
        "douyu", "huya", "twitch",
        # 其他娱乐
        "typora", "markdown",
    ],
    "游戏": [
        "ygenshin", "yuanshen", "genshinimpact",
        "minecraft", "javaw",  # minecraft 常用 javaw 启动
        "steam", "steamwebhelper", "csgo", "cs2", "dota2",
        "league", "lol", "valorant", "riot",
        "epicgames", "origin", "uplay", "battlenet",
        "wegame", "tgp",
        "roblox", "unity", "unreal",
        "game", "play",
        " BetterGI", "bettergi",
        "pcl", "plain craft launcher",
        "atelier",
        "nekopara",
        "angrybirds",
        "vpet",
        "100orange",
    ],
    "学习": [
        "winword", "excel", "powerpnt", "outlook",
        "acrobat", "sumatrapdf", "foxit", "pdf",
        "onenote", "evernote", "wiznote",
        "notion", "obsidian",
        "typora",
        "anki", "xmind", "mindmanager",
        "mathtype", "geogebra",
        "zotero", "endnote",
        "csdn", "zhihu", "runoob", "w3school",
        "bing", "dictionary",
    ],
    "系统": [
        "explorer", "systemsettings", "taskmgr", "mmc",
        "sihost", "ctfmon", "dwm",
        "applicationframehost", "shellexperiencehost",
        "searchhost", "startmenuexperiencehost",
        "credentialuibroker", "lockapp",
        "logonui", "fontdrvhost",
        "runtimebroker", "backgroundtaskhost",
        "windowsinternalcomposableshell",
        "spoolsv", "svchost",
        "registry", "regedit",
        "services", "compmgmt",
        "devicepairing", "devices",
        "snipaste", "sniptool",
        "winrar", "7z", "7zfm",
        "hidevolumeosd",
        "systemsettingsadminflows",
        " 小米云服务",
        "sihost",
    ],
    "网页": [
        "chrome", "msedge", "firefox", "brave", "opera",
        "vivaldi", "arc", "safari",
        "maxthon", "360browser", "360chrome",
        "quark",
        "webview",
    ],
    "工具": [
        # 文件管理
        "totalcommander", "everything", "listary", "files",
        # 截图
        "snipaste", "sniptool", "sharex", "winsnap",
        # 系统工具
        "processhacker", "procmon", "procexp",
        "autoruns", "wireshark", "fiddler",
        "hexedit", "hxd",
        # 压缩
        "7z", "winrar", "bandizip",
        # 下载
        "thunder", "迅雷", "idm", "aria2",
        "motrix",
        # 其他工具
        "calc", "calculator",
        "notepad",  # 记事本是工具不是开发
        "photos",  # Windows 照片
        "paint", "mspaint",
        "charactermap", "charmap",
        "clock", "alarms",
        "soundrecorder",
        "magnifier",
        "narrator",
        "control",
        "printdialog",
        "manictimeclient",
        "bettergi",
        "plain craft launcher 2",
        "loomy",
        "repair_",
        "taskbar",
        "toolbar",
        "tray",
    ],
    "设计": [
        "photoshop", "illustrator", "indesign", "premiere", "afterfx",
        "audition", "lightroom", "bridge",
        "figma", "sketch", "canva",
        "3dsmax", "blender", "cinema4d", "maya",
        "autocad", "sketchup",
        "davinci",
        "gimp", "inkscape", "affinity",
        "coreldraw",
        "adobe",
        "sai",
    ],
}

# 执行分类
categorized = {}
for cat, keywords in rules.items():
    categorized[cat] = set()

for proc in all_procs:
    proc_lower = proc.lower()
    matched = False
    for cat, keywords in rules.items():
        for kw in keywords:
            if kw.lower() in proc_lower or proc_lower.startswith(kw.lower()):
                categorized[cat].add(proc)
                matched = True
                break
        if matched:
            break
    if not matched:
        categorized.setdefault("未分类", set()).add(proc)

# 输出统计
print("\n" + "=" * 60)
print("分类统计")
print("=" * 60)
for cat, procs in sorted(categorized.items(), key=lambda x: -len(x[1])):
    print(f"\n{cat} ({len(procs)} 个):")
    for p in sorted(procs):
        print(f"  {p}")

# 导出 JSON 供后续生成 SQL
result = {cat: sorted(procs) for cat, procs in categorized.items()}
with open(r'Z:\final\code\TimeActivity\classified_procs.json', 'w', encoding='utf-8') as f:
    json.dump(result, f, ensure_ascii=False, indent=2)

print(f"\n\nJSON 已保存到 Z:\\final\\code\\TimeActivity\\classified_procs.json")
