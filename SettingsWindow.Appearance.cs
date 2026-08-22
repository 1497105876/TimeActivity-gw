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

    /// <summary>
    /// "测试连接"按钮：按当前选择的 AI 模式探测服务可用性。
    /// lan 模式 → GET {url}/api/tags 探测 Ollama；custom 模式 → 发送一条最小对话请求。
    /// </summary>
    private async void BtnTestAI_Click(object sender, RoutedEventArgs e)
    {
        string apiUrl = TxtApiUrl.Text.Trim();     // 服务地址
        string apiKey = TxtApiKey.Password;        // 密钥（PasswordBox）
        string model = TxtAIModel.Text.Trim();     // 模型名
        string mode = (CbxAIMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "lan"; // 当前模式

        if (string.IsNullOrEmpty(apiUrl)) // 地址必填
        {
            MessageBox.Show("请先填写服务地址", "提示");
            return;
        }

        BtnTestAI.Content = "测试中..."; // 按钮进入忙碌态
        BtnTestAI.IsEnabled = false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) }; // 10 秒超时

            if (mode == "lan")
            {
                // Ollama 模式:GET /api/tags 检测在线
                using var resp = await http.GetAsync($"{apiUrl.TrimEnd('/')}/api/tags");
                if (resp.IsSuccessStatusCode)
                    MessageBox.Show($"连接成功!Ollama 服务正常运行。\n模型名:{model}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"连接失败,HTTP {resp.StatusCode}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // 自定义模式:POST 一个简单消息测试
                if (!string.IsNullOrEmpty(apiKey)) // 有密钥才加鉴权头
                    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                // 用 JsonSerializer 构造请求体，避免模型名含特殊字符破坏 JSON
                var payloadObj = new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 10 };
                var payload = System.Text.Json.JsonSerializer.Serialize(payloadObj);
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(apiUrl, content);

                if (resp.IsSuccessStatusCode)
                    MessageBox.Show($"连接成功!API 可正常调用。\n模型名:{model}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"连接失败,HTTP {resp.StatusCode}\n{await resp.Content.ReadAsStringAsync()}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (TaskCanceledException) // HttpClient 超时会抛 TaskCanceledException
        {
            MessageBox.Show("连接超时,请检查服务是否已启动", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败:{ex.Message}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    /// 统计截图目录的实际磁盘占用（递归遍历 jpg/png/jpeg 文件求和）。
    /// 目录不存在或异常时显示友好提示。
    /// </summary>
    private void UpdateDiskUsage()
    {
        try
        {
            string dir = TxtScreenshotPath.Text;
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
                TxtDiskUsage.Text = mb >= 1024
                    ? $"{mb / 1024.0:F1} GB ({fileCount} 张)"
                    : $"{mb:F0} MB ({fileCount} 张)";
            }
            else
            {
                TxtDiskUsage.Text = "文件夹不存在";
            }
        }
        catch (Exception ex)
        {
            Logger.Error("截图磁盘占用计算失败", ex);
            TxtDiskUsage.Text = "-"; // 计算失败显示占位符
        }
    }

    /// <summary>
    /// AI 模式下拉框变化：切换到局域网模式时若当前地址为空或是云 API 地址，
    /// 自动填入 Ollama 默认配置；切回自定义模式时清掉 Ollama 默认值让用户填写。
    /// </summary>
    private void AIMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return; // 初始化装载阶段不触发联动
        string mode = CbxAIMode.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "lan" : "lan";
        if (mode == "lan")
        {
            // 局域网共享模式:默认 Ollama 地址
            if (string.IsNullOrWhiteSpace(TxtApiUrl.Text) || TxtApiUrl.Text.Contains("minimax") || TxtApiUrl.Text.Contains("openai"))
            {
                TxtApiUrl.Text = "http://localhost:11434"; // Ollama 默认端口
                TxtApiKey.Password = "";                   // 本地服务无需密钥
                TxtAIModel.Text = "qwen2.5:7b";            // 默认模型
            }
        }
        else
        {
            // 自定义模式:如果当前是 Ollama 地址就清空让用户填
            if (TxtApiUrl.Text.Contains("localhost:11434"))
            {
                TxtApiUrl.Text = "";
                TxtAIModel.Text = "";
            }
        }
        MarkChanged(); // 标记未保存更改
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
