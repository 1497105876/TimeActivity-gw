using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using TimeActivity.Data;
using TimeActivity.Models;
using TimeActivity.Services;

namespace TimeActivity;

// ============================================================================
// SettingsWindow.Appearance.cs — 设置窗口的"外观与 AI/截图参数"部分类
// 职责：
//   1) 分类颜色挑选（BtnPickColor_Click，含界面联动刷新）；
//   2) AI 服务"测试连接"（局域网 Ollama / 自定义 OpenAI 兼容 API 两种模式）；
//   3) 截图占用估算（单张大小、每日总量、磁盘限额换算）与实际磁盘用量统计；
//   4) 各设置项变更事件：模式切换默认值、格式/质量联动、数字输入校验等。
// 协作对象：CategoryRepository(颜色)、AISummaryService 的配置项、
//           ScreenshotService(屏幕尺寸)、MarkChanged()(脏标记,在 Save 部分类)。
// ============================================================================
public partial class SettingsWindow
{
    /// <summary>
    /// 分类列表行内"选色"按钮点击：弹出系统取色器修改该分类颜色，
    /// 同步刷新网格、规则页侧边栏并标记未保存更改。
    /// </summary>
    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;               // 事件源必须是按钮
        if (btn.DataContext is not CategoryItem item) return; // 按钮的上下文是所在行数据

        // 用 WinForms ColorDialog(系统自带选色器,效果好)
        var dlg = new System.Windows.Forms.ColorDialog();
        dlg.FullOpen = true; // 展开完整选色面板
        try
        {
            // 当前颜色设进去
            var current = (Color)ColorConverter.ConvertFromString(item.Color ?? "#808080");
            dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch (Exception ex) { Logger.Error($"颜色解析失败: {item.Color}", ex); } // 旧值非法则保持对话框默认色

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) // 用户确认选择
        {
            item.Color = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"; // 统一转 #RRGGBB 格式
            // 刷新绑定
            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                var idx = cats.IndexOf(item);
                if (idx >= 0) // 替换成新对象以触发 DataGrid 刷新（CategoryItem 未实现 INPC）
                {
                    var tmp = cats[idx];
                    cats[idx] = new CategoryItem { Id = tmp.Id, Name = tmp.Name, Color = item.Color, SortOrder = tmp.SortOrder };
                }
            }
            // 同步刷新分类规则页的侧边栏和面板颜色
            if (_rulesLoaded)
            {
                // 更新 _categories 内存中的颜色
                var cat = _categories.FirstOrDefault(c => c.Name == item.Name);
                if (cat != null) cat.Color = item.Color;
                LoadCategorySidebar();
                BuildRulesPanel();
            }
            MarkChanged(); // 标记有未保存的更改
        }
    }

    // ==================== AI 设置（2026-08-23 重做）====================

    // 服务商预设表：Tag → (默认 Base URL, 模型名提示)
    private static readonly Dictionary<string, (string Base, string ModelHint)> AiPresets = new()
    {
        ["custom"]      = ("", ""),
        ["ollama"]      = ("http://localhost:11434/v1", "qwen2.5:7b"),
        ["ollama-cloud"]= ("https://ollama.com/v1", ""),
        ["lmstudio"]    = ("http://localhost:1234/v1", ""),
        ["deepseek"]    = ("https://api.deepseek.com/v1", "deepseek-chat"),
        ["moonshot"]    = ("https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ["qwen"]        = ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ["minimax"]     = ("https://api.minimaxi.com/v1", "MiniMax-Text-01"),
        ["siliconflow"] = ("https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
    };

    // 各服务商的输入记忆（应用会话内有效）：切走时暂存、切回时还原，
    // 避免来回切换互相覆盖；选"自定义"且无记忆时清空，不残留上次内容。
    private static readonly Dictionary<string, (string Url, string Model, string Key)> AiProviderMemory = new();
    private static string? _currentAiProviderTag;

    /// <summary>读取当前 Key 输入框内容（明文/密文两种状态之一）。</summary>
    private string GetKeyInput() =>
        TxtApiKey.Visibility == Visibility.Visible ? TxtApiKey.Password : TxtApiKeyPlain.Text;

    /// <summary>写入 Key 输入框（自动落到当前可见的那个控件）。</summary>
    private void SetKeyInput(string value)
    {
        if (TxtApiKey.Visibility == Visibility.Visible) TxtApiKey.Password = value;
        else TxtApiKeyPlain.Text = value;
    }

    /// <summary>
    /// 服务商预设切换（2026-08-23 二轮改进）：
    /// 1) 切走前把当前 地址/模型/Key 暂存到该服务商的记忆槽；
    /// 2) 切入时优先还原记忆，无记忆则：自定义=清空（不残留上次内容），其余=填预设默认值；
    /// 3) _currentAiProviderTag 由 LoadSettings 装载后初始化，装载期不触发本逻辑。
    /// </summary>
    private void AIProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CbxAIProvider == null) return; // 装载期不联动
        var newTag = (CbxAIProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(newTag)) return;

        // 1) 暂存切走的服务商当前输入
        if (!string.IsNullOrEmpty(_currentAiProviderTag))
            AiProviderMemory[_currentAiProviderTag] = (TxtApiUrl.Text, CbxAIModel.Text, GetKeyInput());

        // 2) 还原或填默认
        if (AiProviderMemory.TryGetValue(newTag, out var memo))
        {
            TxtApiUrl.Text = memo.Url;
            CbxAIModel.Text = memo.Model;
            SetKeyInput(memo.Key);
        }
        else if (newTag == "custom")
        {
            // 自定义且从未输入过 → 清空，不残留其他服务商的内容
            TxtApiUrl.Text = "";
            CbxAIModel.Text = "";
            SetKeyInput("");
        }
        else if (AiPresets.TryGetValue(newTag, out var p))
        {
            TxtApiUrl.Text = p.Base;
            CbxAIModel.Text = p.ModelHint;
            SetKeyInput("");
        }

        _currentAiProviderTag = newTag;
        MarkChanged();
    }

    /// <summary>Key"显示"开关：在 PasswordBox 与明文 TextBox 间切换并同步值。</summary>
    private void TglShowKey_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtApiKey == null || TxtApiKeyPlain == null) return; // XAML 未就绪
        if (TglShowKey.IsChecked == true)
        {
            TxtApiKeyPlain.Text = TxtApiKey.Password;   // 密文 → 明文
            TxtApiKey.Visibility = Visibility.Collapsed;
            TxtApiKeyPlain.Visibility = Visibility.Visible;
            TglShowKey.Content = "隐藏";
        }
        else
        {
            TxtApiKey.Password = TxtApiKeyPlain.Text;   // 明文 → 密文
            TxtApiKeyPlain.Visibility = Visibility.Collapsed;
            TxtApiKey.Visibility = Visibility.Visible;
            TglShowKey.Content = "显示";
        }
    }

    /// <summary>明文 Key 编辑时同步回 PasswordBox，保证任一状态下保存取值正确。</summary>
    private void TxtApiKeyPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || TxtApiKey == null) return;
        if (TxtApiKeyPlain.Visibility == Visibility.Visible)
            TxtApiKey.Password = TxtApiKeyPlain.Text;
    }

    /// <summary>
    /// "获取模型列表"：GET {base}/models（仅状态码，不消耗 token），成功则填充模型下拉。
    /// </summary>
    private async void BtnFetchModels_Click(object sender, RoutedEventArgs e)
    {
        string apiUrl = TxtApiUrl.Text.Trim();
        if (string.IsNullOrEmpty(apiUrl)) { MessageBox.Show("请先填写接口地址", "提示"); return; }

        BtnFetchModels.IsEnabled = false;
        var old = BtnFetchModels.Content;
        BtnFetchModels.Content = "获取中...";
        try
        {
            var (ok, status, models, err) = await AISummaryService.TryFetchModelsAsync(apiUrl, GetKeyInput());
            if (!ok)
            {
                TxtAITestResult.Text = $"获取失败：{(status == null ? "网络错误：" + err : "HTTP " + status)}";
                return;
            }
            CbxAIModel.Items.Clear();               // 重填下拉
            foreach (var m in models.OrderBy(x => x))
                CbxAIModel.Items.Add(new ComboBoxItem { Content = m });
            TxtAITestResult.Text = $"HTTP {status} · 获取到 {models.Count} 个模型";
            if (models.Count == 0)
                TxtAITestResult.Text += "（列表为空，可手输模型名）";
        }
        finally
        {
            BtnFetchModels.Content = old;
            BtnFetchModels.IsEnabled = true;
        }
    }

    /// <summary>
    /// "测试连接"：按你的要求采用**状态码探测** —— GET {base}/models，
    /// 不真实发送对话、不消耗 token；成功时额外校验所填模型是否在列表中。
    /// </summary>
    private async void BtnTestAI_Click(object sender, RoutedEventArgs e)
    {
        string apiUrl = TxtApiUrl.Text.Trim();     // 接口地址
        string apiKey = GetKeyInput();             // Key（可能为空=本机服务）
        string model = CbxAIModel.Text.Trim();     // 模型名

        if (string.IsNullOrEmpty(apiUrl)) // 地址必填
        {
            TxtAITestResult.Text = "❌ 请先填写接口地址";
            return;
        }

        BtnTestAI.Content = "测试中..."; // 忙碌态
        BtnTestAI.IsEnabled = false;
        TxtAITestResult.Text = "测试中...";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var (ok, status, models, err) = await AISummaryService.TryFetchModelsAsync(apiUrl, apiKey);
            sw.Stop();

            if (!ok && status == null) // 异常类失败（DNS/拒绝连接/超时）
            {
                TxtAITestResult.Text = $"❌ 连接失败：{err}";
                return;
            }
            if (!ok) // HTTP 非 2xx
            {
                string hint = status switch
                {
                    401 or 403 => "（Key 无效或无权限）",
                    404 => "（地址可能缺少 /v1 或服务未开启兼容端点）",
                    _ => ""
                };
                TxtAITestResult.Text = $"❌ HTTP {status} {hint} · 端点:{AISummaryService.BuildModelsEndpoint(apiUrl)}";
                return;
            }

            // 2xx：连接正常；进一步校验模型名是否存在
            string result = $"✅ HTTP {status} · {sw.ElapsedMilliseconds}ms · 模型 {models.Count} 个";
            if (!string.IsNullOrEmpty(model) && models.Count > 0 && !models.Contains(model))
                result += $"\n⚠️ 所填模型「{model}」不在列表中，请核对拼写或点\"获取模型列表\"选择";
            else if (string.IsNullOrEmpty(model))
                result += "\n⚠️ 尚未填写模型名称";
            else
                result += "\n✔ 已确认模型在列表中";
            TxtAITestResult.Text = result;
        }
        finally
        {
            BtnTestAI.Content = "测试连接"; // 恢复按钮文案
            BtnTestAI.IsEnabled = true;     // 恢复可点击
        }
    }

    /// <summary>
    /// 估算单张截图体积（KB）：PNG 无损按 750KB；JPEG 按质量档位取实测均值。
    /// </summary>
    private int GetEstPerShotKB()
    {
        // PNG 无损,文件最大
        bool isPng = CbxScreenshotFormat.SelectedItem is ComboBoxItem fi && fi.Tag?.ToString() == "png";
        if (isPng) return 750; // PNG 2560x1440 约 750KB
        // JPEG 按实际测试数据:high=400KB, medium=275KB, low=180KB
        return GetComboTag(CbxScreenshotQuality) switch
        {
            "high" => 400,
            "low" => 180,
            _ => 275
        };
    }

    /// <summary>
    /// 按当前真实屏幕分辨率折算单张截图大小（以 2560x1440 的估算值为基准等比缩放）。
    /// </summary>
    private int GetActualScreenKB()
    {
        try
        {
            int w = ScreenshotService.GetScreenWidth();   // 当前屏幕宽
            int h = ScreenshotService.GetScreenHeight(); // 当前屏幕高
            double ratio = (double)(w * h) / (2560 * 1440); // 面积比
            return (int)(GetEstPerShotKB() * ratio);      // 基准值×面积比
        }
        catch (Exception ex)
        {
            Logger.Error("获取屏幕大小失败", ex);
            return GetEstPerShotKB(); // 失败退回基准估算
        }
    }

    /// <summary>
    /// 刷新"预计占用"文本：按 定时张数+切换张数 估算每日总量，
    /// 并根据用户设置的容量/天数上限给出可用天数或所需空间提示。
    /// </summary>
    private void UpdateEstimates()
    {
        int perShotKB = GetActualScreenKB();                       // 单张实际估算
        TxtEstSize.Text = $"约 {perShotKB / 1024.0:F1} MB ({perShotKB} KB)";

        string intervalText = CbxScreenshotInterval.Text.Replace("分钟", "").Trim(); // 从下拉文字解析间隔分钟数
        int intervalMin = int.TryParse(intervalText, out int iv) && iv > 0 ? iv : 5; // 非法回退 5 分钟

        int timedShots = 1440 / intervalMin;               // 一天的定时截图数（1440=24h*60min）
        bool onSwitch = ChkScreenshotOnSwitch.IsChecked == true; // 是否开启了切换应用时截屏
        int switchShots = onSwitch ? 100 : 0;              // 切换截屏按经验日均 100 张估算

        int totalShots = timedShots + switchShots; // 每日总张数
        int dailyKB = totalShots * perShotKB;      // 每日总量 KB
        double dailyMB = dailyKB / 1024.0;

        string dailyStr = dailyMB >= 1024 // 超 1GB 用 GB 单位显示
            ? $"约 {dailyMB / 1024.0:F1} GB/天 ({totalShots} 张)"
            : $"约 {dailyMB:F0} MB/天 ({totalShots} 张)";

        if (ChkMaxSize.IsChecked == true && int.TryParse(TxtMaxSize.Text, out int maxMB) && maxMB > 0)
        {
            double days = maxMB / dailyMB; // 容量上限 ÷ 日增量 = 可用天数
            dailyStr += $" → {maxMB}MB 约可用 {days:F0} 天";
        }
        if (ChkMaxAge.IsChecked == true && int.TryParse(TxtMaxAge.Text, out int maxAge) && maxAge > 0)
        {
            double totalMB = dailyMB * maxAge; // 天数上限 × 日增量 = 所需空间
            string totalStr = totalMB >= 1024 ? $"{totalMB / 1024.0:F1} GB" : $"{totalMB:F0} MB";
            dailyStr += $" → {maxAge}天 约需 {totalStr}";
        }

        TxtEstDaily.Text = dailyStr;
    }

    /// <summary>
    /// 统计截图目录的实际磁盘占用。
    /// 2026-08-23 二轮：目录递归扫描可能涉及数千文件，改为后台线程执行，
    /// 结束后回填 UI —— 修复"设置窗口打开后卡一下/很慢"的问题。
    /// </summary>
    private void UpdateDiskUsage()
    {
        string dir = TxtScreenshotPath.Text;   // 先取当前路径快照
        TxtDiskUsage.Text = "统计中...";        // 立即给出反馈
        _ = Task.Run(async () =>
        {
            string result = ComputeDiskUsageText(dir); // 重活放后台
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtDiskUsage.Text = result;        // 回 UI 线程填结果
            }));
        });
    }

    /// <summary>纯计算：给定目录 → 占用描述文本（无 UI 访问，可在线程池执行）。</summary>
    private static string ComputeDiskUsageText(string dir)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                long totalBytes = 0;
                int fileCount = 0;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)) // 递归所有子目录
                {
                    // 只统计图片扩展名
                    if (f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        totalBytes += new FileInfo(f).Length;
                        fileCount++;
                    }
                }
                double mb = totalBytes / (1024.0 * 1024.0); // 换算 MB
                return mb >= 1024
                    ? $"{mb / 1024.0:F1} GB ({fileCount} 张)"
                    : $"{mb:F0} MB ({fileCount} 张)";
            }
            return "文件夹不存在";
        }
        catch (Exception ex)
        {
            Logger.Error("截图磁盘占用计算失败", ex);
            return "-"; // 计算失败显示占位符
        }
    }

    // （原 AIMode_Changed 已随"局域网共享模式"一并移除，2026-08-23：
    //   现在统一为 OpenAI 兼容接口，服务商联动见 AIProvider_Changed）

    /// <summary>小数输入校验：允许数字与一个小数点（用于温度输入框）。</summary>
    private void NumberDecimalOnly_Preview(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c) && c != '.') { e.Handled = true; return; } // 非数字/点直接吞掉
        }
    }

    /// <summary>截图间隔/格式变化：PNG 隐藏质量行、刷新占用估算并标记更改。</summary>
    private void CbxScreenshotInterval_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return; // 装载阶段跳过
        // PNG 格式时隐藏质量选项
        bool isPng = CbxScreenshotFormat.SelectedItem is ComboBoxItem fi && fi.Tag?.ToString() == "png";
        QualityRow.Visibility = isPng ? Visibility.Collapsed : Visibility.Visible; // PNG 无损无质量档位
        UpdateEstimates();
        MarkChanged();
    }

    /// <summary>启用/禁用截图开关：级联禁用截图选项面板。</summary>
    private void ChkEnableScreenshot_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool enabled = ChkEnableScreenshot.IsChecked == true;
        ScreenshotOptionsPanel.IsEnabled = enabled; // 子选项整体可用性跟随开关
        MarkChanged();
    }

    /// <summary>存储限额（容量/天数）输入变化：重算估算并标记更改。</summary>
    private void StorageLimit_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateEstimates();
        MarkChanged();
    }

    /// <summary>数字输入校验：只允许数字字符进入文本框。</summary>
    private void NumberOnly_Preview(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c)) { e.Handled = true; return; } // 非数字直接吞掉
        }
    }

}
