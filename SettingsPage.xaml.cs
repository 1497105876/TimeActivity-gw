using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity;

public partial class SettingsPage : Page
{
    private bool _loading = false;
    private bool _hasChanges = false;

    // 分类列表（用于规则下拉和分类管理）
    private List<CategoryItem> _categories = new();

    public SettingsPage()
    {
        InitializeComponent();
        _loading = true;
        LoadSettings();
        LoadCategories();
        LoadRules();
        UpdateEstimates();
        UpdateDiskUsage();
        ApplyTheme();
        _loading = false;
        _hasChanges = false;
    }

    // ========== 加载设置 ==========

    private void LoadSettings()
    {
        // 追踪设置
        SetComboByTagOrText(CbxSamplingInterval, DatabaseHelper.GetSetting("SamplingInterval", "3"), "秒");
        SetComboByTagOrText(CbxIdleThreshold, DatabaseHelper.GetSetting("IdleThreshold", "300"), "分钟");
        ChkAutoStartTracking.IsChecked = DatabaseHelper.GetSetting("AutoStartTracking", "true") == "true";
        ChkTrackWindowTitle.IsChecked = DatabaseHelper.GetSetting("TrackWindowTitle", "true") == "true";

        // 截图设置
        ChkEnableScreenshot.IsChecked = DatabaseHelper.GetSetting("EnableScreenshot", "false") == "true";
        ChkScreenshotOnSwitch.IsChecked = DatabaseHelper.GetSetting("ScreenshotOnSwitch", "true") == "true";

        string intervalStr = DatabaseHelper.GetSetting("ScreenshotIntervalMinutes", "5");
        SetComboByTagOrText(CbxScreenshotInterval, intervalStr, "分钟");

        // 截图格式
        string fmt = DatabaseHelper.GetSetting("ScreenshotFormat", "jpg");
        foreach (ComboBoxItem item in CbxScreenshotFormat.Items)
        {
            if (item.Tag?.ToString() == fmt) { CbxScreenshotFormat.SelectedItem = item; break; }
        }
        CbxScreenshotFormat.SelectedIndex = fmt == "png" ? 1 : 0;

        SelectComboByTag(CbxScreenshotQuality, DatabaseHelper.GetSetting("ScreenshotQuality", "medium"));
        TxtScreenshotPath.Text = DatabaseHelper.GetSetting("ScreenshotPath",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots"));

        // 存储限制
        ChkMaxSize.IsChecked = DatabaseHelper.GetSetting("EnableMaxSize", "true") == "true";
        TxtMaxSize.Text = DatabaseHelper.GetSetting("MaxScreenshotSizeMB", "5120");
        ChkMaxAge.IsChecked = DatabaseHelper.GetSetting("EnableMaxAge", "true") == "true";
        TxtMaxAge.Text = DatabaseHelper.GetSetting("MaxScreenshotAgeDays", "30");

        // 显示设置
        Chk24Hour.IsChecked = DatabaseHelper.GetSetting("Use24Hour", "true") == "true";
        SelectComboByTag(CbxTheme, DatabaseHelper.GetSetting("Theme", "light"));
        ChkSkipIdle.IsChecked = DatabaseHelper.GetSetting("SkipIdleInStats", "false") == "true";

        // 数据设置
        SetComboByTagOrText(CbxDataRetention, DatabaseHelper.GetSetting("DataRetentionDays", "90"), "天");

        // AI 设置
        ChkEnableAI.IsChecked = DatabaseHelper.GetSetting("EnableAI", "true") == "true";
        string aiMode = DatabaseHelper.GetSetting("AIMode", "lan");
        foreach (ComboBoxItem item in CbxAIMode.Items)
        {
            if (item.Tag?.ToString() == aiMode) { CbxAIMode.SelectedItem = item; break; }
        }
        TxtApiUrl.Text = DatabaseHelper.GetSetting("AIApiUrl", "");
        TxtApiKey.Password = DatabaseHelper.GetSetting("AIApiKey", "");
        TxtAIModel.Text = DatabaseHelper.GetSetting("AIModel", "");

        // AI 总结文件保存设置
        TxtAISummaryPath.Text = DatabaseHelper.GetSetting("AISummaryPath", "");
        TxtAISummaryMaxCount.Text = DatabaseHelper.GetSetting("AISummaryMaxCount", "0");
        TxtAISummaryMaxSizeMB.Text = DatabaseHelper.GetSetting("AISummaryMaxSizeMB", "0");

        // 系统设置
        ChkAutoStart.IsChecked = DatabaseHelper.GetSetting("AutoStartWithWindows", "false") == "true";
        ChkMinimizeToTray.IsChecked = DatabaseHelper.GetSetting("MinimizeToTray", "true") == "true";
    }

    // ========== 分类管理 ==========

    private void LoadCategories()
    {
        _categories = new List<CategoryItem>();
        try
        {
            var cats = DatabaseHelper.GetAllCategories();
            foreach (var cat in cats)
            {
                _categories.Add(new CategoryItem { Id = cat.Id, Name = cat.Name, Color = cat.Color, SortOrder = cat.SortOrder });
            }
        }
        catch { }

        CategoriesGrid.ItemsSource = new ObservableCollection<CategoryItem>(_categories);

        // 更新规则 DataGrid 的分类下拉
        UpdateRuleCategoryColumn();
    }

    private void UpdateRuleCategoryColumn()
    {
        foreach (var col in RulesGrid.Columns)
        {
            if (col is DataGridComboBoxColumn comboCol)
            {
                comboCol.ItemsSource = _categories.Select(c => c.Name).ToList();
            }
        }
    }

    // ========== 分类规则 ==========

    private void LoadRules()
    {
        var rules = DatabaseHelper.GetAllRules();
        var ruleItems = new ObservableCollection<RuleItem>();
        foreach (var r in rules)
        {
            var cat = _categories.FirstOrDefault(c => c.Id == r.CategoryId);
            ruleItems.Add(new RuleItem
            {
                Id = r.Id,
                ProcessName = r.ProcessName ?? "",
                TitleKeyword = r.TitleKeyword ?? "",
                CategoryName = cat?.Name ?? ""
            });
        }
        RulesGrid.ItemsSource = ruleItems;
    }

    // ========== 保存设置 ==========

    public static event Action? SettingsSaved;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 追踪设置
        string samplingText = CbxSamplingInterval.Text.Replace("秒", "").Trim();
        if (int.TryParse(samplingText, out int sv) && sv > 0)
            DatabaseHelper.SetSetting("SamplingInterval", sv.ToString());
        else
            DatabaseHelper.SetSetting("SamplingInterval", "3");

        string idleText = CbxIdleThreshold.Text.Replace("分钟", "").Trim();
        if (int.TryParse(idleText, out int iv) && iv > 0)
            DatabaseHelper.SetSetting("IdleThreshold", (iv * 60).ToString());
        else
            DatabaseHelper.SetSetting("IdleThreshold", "300");

        DatabaseHelper.SetSetting("AutoStartTracking", ChkAutoStartTracking.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("TrackWindowTitle", ChkTrackWindowTitle.IsChecked == true ? "true" : "false");

        // 截图设置
        DatabaseHelper.SetSetting("EnableScreenshot", ChkEnableScreenshot.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("ScreenshotOnSwitch", ChkScreenshotOnSwitch.IsChecked == true ? "true" : "false");

        string intervalText = CbxScreenshotInterval.Text.Replace("分钟", "").Trim();
        if (int.TryParse(intervalText, out int intervalVal) && intervalVal > 0)
            DatabaseHelper.SetSetting("ScreenshotIntervalMinutes", intervalVal.ToString());
        else
            DatabaseHelper.SetSetting("ScreenshotIntervalMinutes", "5");

        DatabaseHelper.SetSetting("ScreenshotFormat", CbxScreenshotFormat.SelectedItem is ComboBoxItem fmtItem ? fmtItem.Tag?.ToString() ?? "jpg" : "jpg");
        DatabaseHelper.SetSetting("ScreenshotQuality", GetComboTag(CbxScreenshotQuality));
        DatabaseHelper.SetSetting("ScreenshotPath", TxtScreenshotPath.Text);

        DatabaseHelper.SetSetting("EnableMaxSize", ChkMaxSize.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("MaxScreenshotSizeMB",
            int.TryParse(TxtMaxSize.Text, out int ms) && ms > 0 ? ms.ToString() : "5120");
        DatabaseHelper.SetSetting("EnableMaxAge", ChkMaxAge.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("MaxScreenshotAgeDays",
            int.TryParse(TxtMaxAge.Text, out int ma) && ma > 0 ? ma.ToString() : "30");

        // 显示设置
        DatabaseHelper.SetSetting("Use24Hour", Chk24Hour.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("Theme", GetComboTag(CbxTheme));
        DatabaseHelper.SetSetting("SkipIdleInStats", ChkSkipIdle.IsChecked == true ? "true" : "false");

        // 数据设置
        string retentionText = CbxDataRetention.Text.Replace("天", "").Replace("永久", "0").Trim();
        if (int.TryParse(retentionText, out int dr) && dr >= 0)
            DatabaseHelper.SetSetting("DataRetentionDays", dr.ToString());
        else
            DatabaseHelper.SetSetting("DataRetentionDays", "90");

        // AI 设置
        DatabaseHelper.SetSetting("EnableAI", ChkEnableAI.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("AIMode", CbxAIMode.SelectedItem is ComboBoxItem aiItem ? aiItem.Tag?.ToString() ?? "lan" : "lan");
        DatabaseHelper.SetSetting("AIApiUrl", TxtApiUrl.Text);
        DatabaseHelper.SetSetting("AIApiKey", TxtApiKey.Password);
        DatabaseHelper.SetSetting("AIModel", TxtAIModel.Text);
        DatabaseHelper.SetSetting("AutoDailySummary", ChkAutoSummary.IsChecked == true ? "true" : "false");

        // AI 总结文件保存
        DatabaseHelper.SetSetting("AISummaryPath", TxtAISummaryPath.Text);
        DatabaseHelper.SetSetting("AISummaryMaxCount", TxtAISummaryMaxCount.Text);
        DatabaseHelper.SetSetting("AISummaryMaxSizeMB", TxtAISummaryMaxSizeMB.Text);

        // 系统设置
        DatabaseHelper.SetSetting("AutoStartWithWindows", ChkAutoStart.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("MinimizeToTray", ChkMinimizeToTray.IsChecked == true ? "true" : "false");

        // 开机自启
        if (ChkAutoStart.IsChecked == true)
            AutoStartHelper.Enable();
        else
            AutoStartHelper.Disable();

        // 保存分类规则
        SaveRules();
        SaveCategories();

        // 通知主窗口重启服务
        SettingsSaved?.Invoke();

        // 刷新预估值
        UpdateEstimates();
        UpdateDiskUsage();

        _hasChanges = false;
        TxtUnsaved.Text = "";

        MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveRules()
    {
        try
        {
            DatabaseHelper.ClearAllRules();
            if (RulesGrid.ItemsSource is ObservableCollection<RuleItem> rules)
            {
                foreach (var r in rules)
                {
                    if (string.IsNullOrWhiteSpace(r.ProcessName) && string.IsNullOrWhiteSpace(r.TitleKeyword))
                        continue;
                    var cat = _categories.FirstOrDefault(c => c.Name == r.CategoryName);
                    if (cat == null) continue;
                    DatabaseHelper.InsertRule(r.ProcessName ?? "", r.TitleKeyword ?? "", cat.Id);
                }
            }
        }
        catch { }
    }

    private void SaveCategories()
    {
        try
        {
            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                foreach (var c in cats)
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    DatabaseHelper.UpdateOrInsertCategory(c.Id, c.Name, c.Color ?? "#808080", c.SortOrder);
                }
            }
        }
        catch { }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        LoadSettings();
        LoadCategories();
        LoadRules();
        UpdateEstimates();
        UpdateDiskUsage();
        ApplyTheme();
        _loading = false;
        _hasChanges = false;
        TxtUnsaved.Text = "";
    }

    // ========== 截图路径浏览 ==========

    private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存路径",
            InitialDirectory = TxtScreenshotPath.Text ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            TxtScreenshotPath.Text = dialog.FolderName;
            UpdateDiskUsage();
            MarkChanged();
        }
    }

    // ========== AI 总结路径浏览 ==========

    private void BtnBrowseAISummaryPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 AI 总结保存路径",
            InitialDirectory = TxtAISummaryPath.Text ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            TxtAISummaryPath.Text = dialog.FolderName;
            MarkChanged();
        }
    }

    // ========== 预估占用大小 ==========

    private int GetEstPerShotKB()
    {
        // PNG 比 JPEG 大很多
        bool isPng = CbxScreenshotFormat.SelectedItem is ComboBoxItem fi && fi.Tag?.ToString() == "png";
        if (isPng) return 500; // PNG 2560x1440 约 500KB
        return GetComboTag(CbxScreenshotQuality) switch
        {
            "high" => 150,
            "low" => 40,
            _ => 80
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
        catch
        {
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
        catch
        {
            TxtDiskUsage.Text = "-";
        }
    }

    // ========== 主题切换 ==========

    private void ApplyTheme()
    {
        string theme = GetComboTag(CbxTheme);
        if (theme == "auto")
        {
            // 跟随系统：通过注册表判断深浅色
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v)
                    theme = v == 0 ? "dark" : "light";
            }
            catch { }
        }

        if (theme == "dark")
        {
            Resources["PageBg"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            Resources["PanelBg"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            Resources["TextFG"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        }
        else
        {
            Resources["PageBg"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            Resources["PanelBg"] = Brushes.White;
            Resources["TextFG"] = Brushes.Black;
            Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
        }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyTheme();
        MarkChanged();
    }

    // ========== AI 模式切换 ==========

    private void AIMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string mode = CbxAIMode.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "lan" : "lan";
        if (mode == "lan")
        {
            // 局域网共享：默认 Ollama 地址
            if (string.IsNullOrWhiteSpace(TxtApiUrl.Text) || TxtApiUrl.Text.Contains("minimax") || TxtApiUrl.Text.Contains("openai"))
            {
                TxtApiUrl.Text = "http://localhost:11434";
                TxtApiKey.Password = "";
                TxtAIModel.Text = "qwen2.5:7b";
            }
        }
        else
        {
            // 自定义：清空让用户填
            if (TxtApiUrl.Text.Contains("localhost:11434"))
            {
                TxtApiUrl.Text = "";
                TxtAIModel.Text = "";
            }
        }
        MarkChanged();
    }

    // ========== 事件 ==========

    private void CbxScreenshotInterval_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateEstimates();
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

    private void RulesGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MarkChanged();
    }

    private void CategoriesGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MarkChanged();
    }

    // ========== 未保存提示 ==========

    private void MarkChanged()
    {
        if (_loading) return;
        _hasChanges = true;
        TxtUnsaved.Text = "● 有未保存的更改";
    }

    // ========== 清空数据 ==========

    private void BtnClearData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要清空所有活动记录、截图和统计数据吗？\n此操作不可恢复！",
            "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var result2 = MessageBox.Show(
            "再次确认：真的要删除所有数据吗？",
            "再次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result2 != MessageBoxResult.Yes) return;

        DatabaseHelper.ClearAllData();
        UpdateDiskUsage();
        MessageBox.Show("所有数据已清空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ========== 恢复默认 ==========

    private void BtnRestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "恢复所有设置为默认值？\n（分类规则和分类管理不会被重置）",
            "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // 重置数据库设置到默认
        var defaults = new Dictionary<string, string>
        {
            {"SamplingInterval", "3"},
            {"IdleThreshold", "300"},
            {"AutoStartTracking", "true"},
            {"TrackWindowTitle", "true"},
            {"EnableScreenshot", "false"},
            {"ScreenshotOnSwitch", "true"},
            {"ScreenshotIntervalMinutes", "5"},
            {"ScreenshotFormat", "jpg"},
            {"ScreenshotPath", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots")},
            {"ScreenshotQuality", "medium"},
            {"EnableMaxSize", "true"},
            {"MaxScreenshotSizeMB", "5120"},
            {"EnableMaxAge", "true"},
            {"MaxScreenshotAgeDays", "30"},
            {"Use24Hour", "true"},
            {"Theme", "light"},
            {"SkipIdleInStats", "false"},
            {"DataRetentionDays", "90"},
            {"EnableAI", "true"},
            {"AIMode", "lan"},
            {"AIApiUrl", "http://localhost:11434"},
            {"AIApiKey", ""},
            {"AIModel", "qwen2.5:7b"},
            {"AutoDailySummary", "true"},
            {"AutoStartWithWindows", "false"},
            {"MinimizeToTray", "true"},
        };
        foreach (var kv in defaults)
            DatabaseHelper.SetSetting(kv.Key, kv.Value);

        _loading = true;
        LoadSettings();
        UpdateEstimates();
        UpdateDiskUsage();
        ApplyTheme();
        _loading = false;
        _hasChanges = true;
        TxtUnsaved.Text = "● 已恢复默认，请点保存生效";
    }

    // ========== 导入导出 ==========

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出设置",
            Filter = "JSON 文件|*.json",
            FileName = "timeactivity_settings.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var settings = DatabaseHelper.GetAllSettings();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
            MessageBox.Show($"设置已导出到\n{dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入设置",
            Filter = "JSON 文件|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (settings == null) { MessageBox.Show("文件内容为空", "错误"); return; }

            foreach (var kv in settings)
                DatabaseHelper.SetSetting(kv.Key, kv.Value);

            _loading = true;
            LoadSettings();
            LoadCategories();
            LoadRules();
            UpdateEstimates();
            UpdateDiskUsage();
            ApplyTheme();
            _loading = false;
            _hasChanges = false;
            TxtUnsaved.Text = "";
            MessageBox.Show("设置已导入", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ========== 辅助方法 ==========

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    /// <summary>
    /// 用 Tag 或文本匹配 ComboBox 项，匹配不上就把文本写进去（适用于可编辑 ComboBox）
    /// </summary>
    private static void SetComboByTagOrText(ComboBox combo, string value, string suffix = "")
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Content?.ToString()?.Replace(suffix, "").Trim() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        // 匹配不上：直接写文本（可编辑模式）
        combo.Text = value;
    }

    private static string GetComboTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "";
        return "";
    }
}

// ========== 数据模型 ==========

public class RuleItem
{
    public int Id { get; set; }
    public string ProcessName { get; set; } = "";
    public string TitleKeyword { get; set; } = "";
    public string CategoryName { get; set; } = "";
}

public class CategoryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#808080";
    public int SortOrder { get; set; }
}
