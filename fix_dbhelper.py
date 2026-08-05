import sys
sys.stdout.reconfigure(encoding='utf-8')

filepath = r'Z:\final\code\TimeActivity\Data\DatabaseHelper.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# 旧代码块（从"// 默认进程规则"到"Logger.Info("预置分类规则已写入 Rules 表");"）
old_block = '''                // 默认进程规则（IsCustom=0 表示预置不可删）
                var procRules = new[]
                {
                    ("devenv", "开发"), ("Code", "开发"), ("idea64", "开发"),
                    ("pycharm64", "开发"), ("cmd", "开发"), ("powershell", "开发"),
                    ("WindowsTerminal", "开发"), ("git", "开发"),
                    ("WeChat", "社交"), ("QQ", "社交"), ("Discord", "社交"), ("Telegram", "社交"),
                    ("Spotify", "娱乐"), ("QQMusic", "娱乐"), ("MusicPlayer2", "娱乐"),
                    ("WINWORD", "学习"), ("EXCEL", "学习"), ("POWERPNT", "学习"),
                    ("Acrobat", "学习"), ("SumatraPDF", "学习"),
                    ("explorer", "系统"), ("SystemSettings", "系统"), ("taskmgr", "系统"),
                    ("chrome", "网页"), ("msedge", "网页"), ("firefox", "网页"),
                    ("brave", "网页"), ("opera", "网页"),
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

                // 默认标题关键词规则
                var titleRules = new[]
                {
                    ("B站", "娱乐"), ("bilibili", "娱乐"), ("YouTube", "娱乐"),
                    ("抖音", "娱乐"), ("斗鱼", "娱乐"), ("虎牙", "娱乐"), ("原神", "娱乐"),
                    ("GitHub", "开发"), ("Stack Overflow", "开发"),
                    ("CSDN", "学习"), ("知乎", "学习"), ("菜鸟教程", "学习"),
                };
                foreach (var (kw, cat) in titleRules)
                {
                    if (catMap.TryGetValue(cat, out int catId))
                    {
                        using var ins = new SqliteCommand(
                            "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES ('', @k, @c, 0)", conn);
                        ins.Parameters.AddWithValue("@k", kw);
                        ins.Parameters.AddWithValue("@c", catId);
                        ins.ExecuteNonQuery();
                    }
                }

                Logger.Info("预置分类规则已写入 Rules 表");'''

# 新代码块
new_block = '''                // 预置进程规则（IsCustom=0 表示预置不可删，全部进程名精确匹配）
                // 完整列表见 Z:\\final\\相关方案\\26-8-05-23-初始规则数据完整版.md
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

                Logger.Info($"预置 {procRules.Length} 条分类规则已写入 Rules 表");'''

if old_block in content:
    content = content.replace(old_block, new_block)
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("替换成功！")
else:
    print("未找到旧代码块！")
    # 找特征行
    for line in old_block.split('\n')[:5]:
        if line in content:
            print(f"  找到: {line.strip()}")
        else:
            print(f"  未找到: {line.strip()}")
