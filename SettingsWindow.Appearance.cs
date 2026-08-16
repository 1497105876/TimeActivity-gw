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

public partial class SettingsWindow
{
    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not CategoryItem item) return;

        // 用 WinForms ColorDialog(系统自带选色器,效果好)
        var dlg = new System.Windows.Forms.ColorDialog();
        dlg.FullOpen = true; // 展开完整选色面板
        try
        {
            // 当前颜色设进去
            var current = (Color)ColorConverter.ConvertFromString(item.Color ?? "#808080");
            dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch (Exception ex) { Logger.Error($"颜色解析失败: {item.Color}", ex); }

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            item.Color = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            // 刷新绑定
            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                var idx = cats.IndexOf(item);
                if (idx >= 0)
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
            MarkChanged();
        }
    }

    private async void BtnTestAI_Click(object sender, RoutedEventArgs e)
    {
        string apiUrl = TxtApiUrl.Text.Trim();
        string apiKey = TxtApiKey.Password;
        string model = TxtAIModel.Text.Trim();
        string mode = (CbxAIMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "lan";

        if (string.IsNullOrEmpty(apiUrl))
        {
            MessageBox.Show("请先填写服务地址", "提示");
            return;
        }

        BtnTestAI.Content = "测试中...";
        BtnTestAI.IsEnabled = false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

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
                if (!string.IsNullOrEmpty(apiKey))
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
        catch (TaskCanceledException)
        {
            MessageBox.Show("连接超时,请检查服务是否已启动", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败:{ex.Message}", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnTestAI.Content = "测试连接";
            BtnTestAI.IsEnabled = true;
        }
    }

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

    private int GetActualScreenKB()
    {
        try
        {
            int w = ScreenshotService.GetScreenWidth();
            int h = ScreenshotService.GetScreenHeight();
            double ratio = (double)(w * h) / (2560 * 1440);
            return (int)(GetEstPerShotKB() * ratio);
        }
        catch (Exception ex)
        {
            Logger.Error("获取屏幕大小失败", ex);
            return GetEstPerShotKB();
        }
    }

    private void UpdateEstimates()
    {
        int perShotKB = GetActualScreenKB();
        TxtEstSize.Text = $"约 {perShotKB / 1024.0:F1} MB ({perShotKB} KB)";

        string intervalText = CbxScreenshotInterval.Text.Replace("分钟", "").Trim();
        int intervalMin = int.TryParse(intervalText, out int iv) && iv > 0 ? iv : 5;

        int timedShots = 1440 / intervalMin;
        bool onSwitch = ChkScreenshotOnSwitch.IsChecked == true;
        int switchShots = onSwitch ? 100 : 0;

        int totalShots = timedShots + switchShots;
        int dailyKB = totalShots * perShotKB;
        double dailyMB = dailyKB / 1024.0;

        string dailyStr = dailyMB >= 1024
            ? $"约 {dailyMB / 1024.0:F1} GB/天 ({totalShots} 张)"
            : $"约 {dailyMB:F0} MB/天 ({totalShots} 张)";

        if (ChkMaxSize.IsChecked == true && int.TryParse(TxtMaxSize.Text, out int maxMB) && maxMB > 0)
        {
            double days = maxMB / dailyMB;
            dailyStr += $" → {maxMB}MB 约可用 {days:F0} 天";
        }
        if (ChkMaxAge.IsChecked == true && int.TryParse(TxtMaxAge.Text, out int maxAge) && maxAge > 0)
        {
            double totalMB = dailyMB * maxAge;
            string totalStr = totalMB >= 1024 ? $"{totalMB / 1024.0:F1} GB" : $"{totalMB:F0} MB";
            dailyStr += $" → {maxAge}天 约需 {totalStr}";
        }

        TxtEstDaily.Text = dailyStr;
    }

    private void UpdateDiskUsage()
    {
        try
        {
            string dir = TxtScreenshotPath.Text;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                long totalBytes = 0;
                int fileCount = 0;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        totalBytes += new FileInfo(f).Length;
                        fileCount++;
                    }
                }
                double mb = totalBytes / (1024.0 * 1024.0);
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
            TxtDiskUsage.Text = "-";
        }
    }

    private void AIMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string mode = CbxAIMode.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "lan" : "lan";
        if (mode == "lan")
        {
            // 局域网共享模式:默认 Ollama 地址
            if (string.IsNullOrWhiteSpace(TxtApiUrl.Text) || TxtApiUrl.Text.Contains("minimax") || TxtApiUrl.Text.Contains("openai"))
            {
                TxtApiUrl.Text = "http://localhost:11434";
                TxtApiKey.Password = "";
                TxtAIModel.Text = "qwen2.5:7b";
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
        MarkChanged();
    }

    private void CbxScreenshotInterval_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // PNG 格式时隐藏质量选项
        bool isPng = CbxScreenshotFormat.SelectedItem is ComboBoxItem fi && fi.Tag?.ToString() == "png";
        QualityRow.Visibility = isPng ? Visibility.Collapsed : Visibility.Visible;
        UpdateEstimates();
        MarkChanged();
    }

    private void ChkEnableScreenshot_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool enabled = ChkEnableScreenshot.IsChecked == true;
        ScreenshotOptionsPanel.IsEnabled = enabled;
        MarkChanged();
    }

    private void StorageLimit_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateEstimates();
        MarkChanged();
    }

    private void NumberOnly_Preview(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c)) { e.Handled = true; return; }
        }
    }

}
