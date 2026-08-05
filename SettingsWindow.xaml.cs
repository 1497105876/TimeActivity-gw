using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity;

public partial class SettingsWindow : Window
{
    private bool _loading = false;
    private bool _hasChanges = false;
    private Dictionary<string, string> _originalSettings = new();

    // 分类列表（用于规则下拉和分类管理）
    private List<CategoryItem> _categories = new();

    // 分类名列表（供 DataGridComboBoxColumn 绑定用）
    public List<string> _categoryNames = new();

    public SettingsWindow(string initialSection = null)
    {
        try
        {
        InitializeComponent();
        _loading = true;
        LoadSettings();
        LoadCategories();
        LoadRules();
        UpdateEstimates();
        UpdateDiskUsage();
        _loading = false;
        _hasChanges = false;
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"SettingsWindow 构造失败:\n{ex.Message}\n\n{ex.StackTrace}");
            throw;
        }

        // 根据初始参数选中对应导航项
        if (!string.IsNullOrEmpty(initialSection))
        {
            int index = initialSection.ToLower() switch
            {
                "tracking" => 0,
                "screenshot" => 1,
                "rules" => 2,
                "categories" => 3,
                "display" => 4,
                "data" => 5,
                "ai" => 6,
                "system" => 7,
                "io" => 8,
                _ => -1
            };
            if (index >= 0 && NavList != null)
            {
                NavList.SelectedIndex = index;
            }
        }
    }

    // ========== 侧边栏导航 ==========

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList == null || PanelTracking == null) return;

        // 先全部隐藏
        PanelTracking.Visibility = Visibility.Collapsed;
        PanelScreenshot.Visibility = Visibility.Collapsed;
        PanelRules.Visibility = Visibility.Collapsed;
        PanelCategories.Visibility = Visibility.Collapsed;
        PanelDisplay.Visibility = Visibility.Collapsed;
        PanelData.Visibility = Visibility.Collapsed;
        PanelAI.Visibility = Visibility.Collapsed;
        PanelSystem.Visibility = Visibility.Collapsed;
        PanelIO.Visibility = Visibility.Collapsed;

        // 根据选中索引显示对应面板
        switch (NavList.SelectedIndex)
        {
            case 0: PanelTracking.Visibility = Visibility.Visible; break;
            case 1: PanelScreenshot.Visibility = Visibility.Visible; break;
            case 2: PanelRules.Visibility = Visibility.Visible; break;
            case 3: PanelCategories.Visibility = Visibility.Visible; break;
            case 4: PanelDisplay.Visibility = Visibility.Visible; break;
            case 5: PanelData.Visibility = Visibility.Visible; break;
            case 6: PanelAI.Visibility = Visibility.Visible; break;
            case 7: PanelSystem.Visibility = Visibility.Visible; break;
            case 8: PanelIO.Visibility = Visibility.Visible; break;
        }
    }

    // ========== 加载设置 ==========

    private void LoadSettings()
    {
        // 追踪设置
        SetComboByTagOrText(CbxSamplingInterval, SettingsRepository.Get("PollIntervalSeconds", "3"), "秒");
        SetComboByTagOrText(CbxIdleThreshold, SettingsRepository.Get("IdleThresholdSeconds", "300"), "分钟");
        // 如果 Tag 匹配不上（自定义值），把秒转回分钟显示
        if (CbxIdleThreshold.SelectedIndex == -1)
        {
            if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idleSec))
                CbxIdleThreshold.Text = (idleSec / 60).ToString();
        }
        ChkAutoStartTracking.IsChecked = SettingsRepository.Get("AutoStartTracking", "true") == "true";

        // 截图设置
        ChkEnableScreenshot.IsChecked = SettingsRepository.Get("EnableScreenshot", "false") == "true";
        ChkScreenshotOnSwitch.IsChecked = SettingsRepository.Get("ScreenshotOnSwitch", "true") == "true";

        string intervalStr = SettingsRepository.Get("ScreenshotIntervalMinutes", "5");
        SetComboByTagOrText(CbxScreenshotInterval, intervalStr, "分钟");

        // 截图格式
        string fmt = SettingsRepository.Get("ScreenshotFormat", "jpg");
        foreach (ComboBoxItem item in CbxScreenshotFormat.Items)
        {
            if (item.Tag?.ToString() == fmt) { CbxScreenshotFormat.SelectedItem = item; break; }
        }
        CbxScreenshotFormat.SelectedIndex = fmt == "png" ? 1 : 0;

        SelectComboByTag(CbxScreenshotQuality, SettingsRepository.Get("ScreenshotQuality", "medium"));
        TxtScreenshotPath.Text = SettingsRepository.Get("ScreenshotPath",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots"));

        // 存储限制
        ChkMaxSize.IsChecked = SettingsRepository.Get("EnableMaxSize", "true") == "true";
        TxtMaxSize.Text = SettingsRepository.Get("MaxScreenshotSizeMB", "5120");
        ChkMaxAge.IsChecked = SettingsRepository.Get("EnableMaxAge", "true") == "true";
        TxtMaxAge.Text = SettingsRepository.Get("MaxScreenshotAgeDays", "30");

        // 数据设置
        SetComboByTagOrText(CbxDataRetention, SettingsRepository.Get("DataRetentionDays", "90"), "天");

        // AI 设置
        ChkEnableAI.IsChecked = SettingsRepository.Get("EnableAI", "true") == "true";
        string aiMode = SettingsRepository.Get("AIMode", "lan");
        foreach (ComboBoxItem item in CbxAIMode.Items)
        {
            if (item.Tag?.ToString() == aiMode) { CbxAIMode.SelectedItem = item; break; }
        }
        TxtApiUrl.Text = SettingsRepository.Get("AIApiUrl", "");
        TxtApiKey.Password = SettingsRepository.Get("AIApiKey", "");
        TxtAIModel.Text = SettingsRepository.Get("AIModel", "");

        // AI 总结文件保存设置
        TxtAISummaryPath.Text = SettingsRepository.Get("AISummaryPath", "");
        TxtAISummaryMaxCount.Text = SettingsRepository.Get("AISummaryMaxCount", "0");
        TxtAISummaryMaxSizeMB.Text = SettingsRepository.Get("AISummaryMaxSizeMB", "0");

        // 系统设置
        ChkAutoStart.IsChecked = SettingsRepository.Get("AutoStartWithWindows", "false") == "true";
        ChkMinimizeToTray.IsChecked = SettingsRepository.Get("MinimizeToTray", "true") == "true";
    }

    // ========== 分类管理 ==========

    private void LoadCategories()
    {
        _categories = new List<CategoryItem>();
        try
        {
            var cats = CategoryRepository.GetAll();
            foreach (var cat in cats)
            {
                _categories.Add(new CategoryItem { Id = cat.Id, Name = cat.Name, Color = cat.Color, SortOrder = cat.SortOrder });
            }
        }
        catch { }

        CategoriesGrid.ItemsSource = new ObservableCollection<CategoryItem>(_categories);

        // 更新分类名列表供规则下拉用
        _categoryNames = _categories.Select(c => c.Name).ToList();

        // 加载分类筛选下拉
        if (CbxRuleFilter != null)
        {
            CbxRuleFilter.Items.Clear();
            CbxRuleFilter.Items.Add(new ComboBoxItem { Content = "全部分类", Tag = "" });
            foreach (var c in _categories)
                CbxRuleFilter.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Name });
            CbxRuleFilter.SelectedIndex = 0;
        }

        LoadCategorySidebar();
    }

    private void LoadCategorySidebar()
    {
        if (CategorySidebar == null) return;
        var sidebarItems = new ObservableCollection<CategoryItem>();
        var rules = RuleRepository.GetAll();
        foreach (var c in _categories)
        {
            int count = rules.Count(r => r.CategoryId == c.Id);
            sidebarItems.Add(new CategoryItem { Id = c.Id, Name = c.Name, Color = c.Color, SortOrder = c.SortOrder, Count = count });
        }
        CategorySidebar.ItemsSource = sidebarItems;
    }

    // ========== 分类规则 ==========

    private async void LoadRules()
    {
        RulesListView.ItemsSource = null;
        await Task.Run(() =>
        {
            var rules = RuleRepository.GetAll();
            var ruleItems = new ObservableCollection<RuleItem>();
            foreach (var r in rules)
            {
                var cat = _categories.FirstOrDefault(c => c.Id == r.CategoryId);
                ruleItems.Add(new RuleItem
                {
                    Id = r.Id,
                    ProcessName = r.ProcessName ?? "",
                    TitleKeyword = r.TitleKeyword ?? "",
                    CategoryName = cat?.Name ?? "",
                    IsCustom = r.IsCustom
                });
            }
            _allRules = ruleItems;
        });
        ApplyRuleFilter();
    }

    private ObservableCollection<RuleItem> _allRules = new();

    private void ApplyRuleFilter()
    {
        if (RulesListView == null) return;
        var filtered = _allRules.AsEnumerable();

        // 搜索过滤
        if (TxtRuleSearch != null && !string.IsNullOrWhiteSpace(TxtRuleSearch.Text))
        {
            var keyword = TxtRuleSearch.Text.Trim().ToLower();
            filtered = filtered.Where(r => r.ProcessName.ToLower().Contains(keyword));
        }

        // 分类过滤
        if (CbxRuleFilter != null && CbxRuleFilter.SelectedItem is ComboBoxItem item && item.Tag?.ToString() != "")
        {
            var catName = item.Tag.ToString();
            filtered = filtered.Where(r => r.CategoryName == catName);
        }

        RulesListView.ItemsSource = new ObservableCollection<RuleItem>(filtered);
    }

    // 新增规则
    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        var newRule = new RuleItem { ProcessName = "", CategoryName = _categories.FirstOrDefault()?.Name ?? "", IsCustom = true };
        _allRules.Add(newRule);
        ApplyRuleFilter();
        MarkChanged();
    }

    // 搜索/筛选
    private void TxtRuleSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyRuleFilter();
    }

    private void CbxRuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyRuleFilter();
    }

    private void CategorySidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategorySidebar == null) return;
        if (CategorySidebar.SelectedItem is CategoryItem cat)
        {
            // 点击左侧分类 → 右侧筛选该分类
            for (int i = 0; i < CbxRuleFilter.Items.Count; i++)
            {
                if (CbxRuleFilter.Items[i] is ComboBoxItem item && item.Tag?.ToString() == cat.Name)
                {
                    CbxRuleFilter.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void CategorySidebar_Drop(object sender, DragEventArgs e)
    {
        // 拖拽功能后续实现
        if (e.Data.GetData(typeof(RuleItem)) is RuleItem droppedRule)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CategoryItem targetCat)
            {
                droppedRule.CategoryName = targetCat.Name;
                ApplyRuleFilter();
                MarkChanged();
            }
        }
    }

    private void RulesListView_Drop(object sender, DragEventArgs e)
    {
        // 拖拽功能后续实现
    }

    // ========== 保存设置 ==========

    public static event Action? SettingsSaved;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DoSave();
        MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        this.Close();
    }

    private void SaveRules()
    {
        try
        {
            // 全删全插（预置规则修改也能生效）
            using var conn = new SqliteConnection(DatabaseHelper.ConnectionString);
            conn.Open();
            using var del = new SqliteCommand("DELETE FROM Rules", conn);
            del.ExecuteNonQuery();

            foreach (var r in _allRules)
            {
                if (string.IsNullOrWhiteSpace(r.ProcessName))
                    continue;
                var cat = _categories.FirstOrDefault(c => c.Name == r.CategoryName);
                if (cat == null) continue;

                using var ins = new SqliteCommand(
                    "INSERT INTO Rules (ProcessName, TitleKeyword, CategoryId, IsCustom) VALUES (@p, @k, @c, @ic)", conn);
                ins.Parameters.AddWithValue("@p", r.ProcessName ?? "");
                ins.Parameters.AddWithValue("@k", (object?)r.TitleKeyword ?? DBNull.Value);
                ins.Parameters.AddWithValue("@c", cat.Id);
                ins.Parameters.AddWithValue("@ic", r.IsCustom ? 1 : 0);
                ins.ExecuteNonQuery();
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
                    // Id<=0 表示新分类，插入后获新 Id
                    // Id>0 更新已有分类
                    CategoryRepository.UpdateOrInsert(c.Id, c.Name, c.Color ?? "#808080", c.SortOrder);
                }
            }
        }
        catch { }
    }

    // ========== 删行按钮 + 颜色选择器 ==========

    private void BtnDeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is RuleItem rule)
        {
            if (!rule.IsCustom) return; // 预置规则不可删

            _allRules.Remove(rule);
            ApplyRuleFilter();
            MarkChanged();
        }
    }

    private void BtnDeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CategoryItem cat)
        {
            if (cat.Id <= 8) return; // 预置分类不可删

            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                cats.Remove(cat);
                MarkChanged();
            }
        }
    }

    // 颜色选择器
    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not CategoryItem item) return;

        // 用 WinForms ColorDialog（系统自带选色器，效果好）
        var dlg = new System.Windows.Forms.ColorDialog();
        dlg.FullOpen = true; // 展开完整选色面板
        try
        {
            // 当前颜色设进去
            var current = (Color)ColorConverter.ConvertFromString(item.Color ?? "#808080");
            dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch { }

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
            MarkChanged();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // 不保存直接关窗
        this.Close();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        // 和保存一样逻辑，但不关窗
        DoSave();
    }

    private void DoSave()
    {
        // 追踪设置
        string samplingText = CbxSamplingInterval.Text.Replace("秒", "").Trim();
        if (int.TryParse(samplingText, out int sv) && sv > 0)
            SettingsRepository.Set("PollIntervalSeconds", sv.ToString());
        else
            SettingsRepository.Set("PollIntervalSeconds", "3");

        string idleText = CbxIdleThreshold.Text.Replace("分钟", "").Trim();
        if (int.TryParse(idleText, out int iv) && iv > 0)
            SettingsRepository.Set("IdleThresholdSeconds", (iv * 60).ToString());
        else
            SettingsRepository.Set("IdleThresholdSeconds", "300");

        SettingsRepository.Set("AutoStartTracking", ChkAutoStartTracking.IsChecked == true ? "true" : "false");

        // 截图设置
        SettingsRepository.Set("EnableScreenshot", ChkEnableScreenshot.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("ScreenshotOnSwitch", ChkScreenshotOnSwitch.IsChecked == true ? "true" : "false");

        string intervalText = CbxScreenshotInterval.Text.Replace("分钟", "").Trim();
        if (int.TryParse(intervalText, out int intervalVal) && intervalVal > 0)
            SettingsRepository.Set("ScreenshotIntervalMinutes", intervalVal.ToString());
        else
            SettingsRepository.Set("ScreenshotIntervalMinutes", "5");

        SettingsRepository.Set("ScreenshotFormat", CbxScreenshotFormat.SelectedItem is ComboBoxItem fmtItem ? fmtItem.Tag?.ToString() ?? "jpg" : "jpg");
        SettingsRepository.Set("ScreenshotQuality", GetComboTag(CbxScreenshotQuality));
        SettingsRepository.Set("ScreenshotPath", TxtScreenshotPath.Text);

        SettingsRepository.Set("EnableMaxSize", ChkMaxSize.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MaxScreenshotSizeMB",
            int.TryParse(TxtMaxSize.Text, out int ms) && ms > 0 ? ms.ToString() : "5120");
        SettingsRepository.Set("EnableMaxAge", ChkMaxAge.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MaxScreenshotAgeDays",
            int.TryParse(TxtMaxAge.Text, out int ma) && ma > 0 ? ma.ToString() : "30");

        // 数据设置
        string retentionText = CbxDataRetention.Text.Replace("天", "").Replace("永久", "0").Trim();
        if (int.TryParse(retentionText, out int dr) && dr >= 0)
            SettingsRepository.Set("DataRetentionDays", dr.ToString());
        else
            SettingsRepository.Set("DataRetentionDays", "90");

        // AI 设置
        SettingsRepository.Set("EnableAI", ChkEnableAI.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("AIMode", CbxAIMode.SelectedItem is ComboBoxItem aiItem ? aiItem.Tag?.ToString() ?? "lan" : "lan");
        SettingsRepository.Set("AIApiUrl", TxtApiUrl.Text);
        SettingsRepository.Set("AIApiKey", TxtApiKey.Password);
        SettingsRepository.Set("AIModel", TxtAIModel.Text);
        SettingsRepository.Set("AutoDailySummary", ChkAutoSummary.IsChecked == true ? "true" : "false");

        // AI 总结文件保存
        SettingsRepository.Set("AISummaryPath", TxtAISummaryPath.Text);
        SettingsRepository.Set("AISummaryMaxCount", TxtAISummaryMaxCount.Text);
        SettingsRepository.Set("AISummaryMaxSizeMB", TxtAISummaryMaxSizeMB.Text);

        // 系统设置
        SettingsRepository.Set("AutoStartWithWindows", ChkAutoStart.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MinimizeToTray", ChkMinimizeToTray.IsChecked == true ? "true" : "false");

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
        BtnApply.IsEnabled = false;
        SaveSnapshot();
    }

    /// <summary>
    /// 保存当前设置的快照，用于后续对比是否有变更
    /// </summary>
    private void SaveSnapshot()
    {
        _originalSettings = GetCurrentSettingsSnapshot();
    }

    /// <summary>
    /// 收集当前 UI 上所有设置项的值
    /// </summary>
    private Dictionary<string, string> GetCurrentSettingsSnapshot()
    {
        var snap = new Dictionary<string, string>();
        snap["PollIntervalSeconds"] = CbxSamplingInterval.Text ?? "";
        snap["IdleThresholdSeconds"] = CbxIdleThreshold.Text ?? "";
        snap["AutoStartTracking"] = (ChkAutoStartTracking.IsChecked == true).ToString().ToLower();
        snap["EnableScreenshot"] = (ChkEnableScreenshot.IsChecked == true).ToString().ToLower();
        snap["ScreenshotOnSwitch"] = (ChkScreenshotOnSwitch.IsChecked == true).ToString().ToLower();
        snap["ScreenshotIntervalMinutes"] = CbxScreenshotInterval.Text ?? "";
        snap["ScreenshotQuality"] = GetComboTag(CbxScreenshotQuality);
        snap["ScreenshotFormat"] = GetComboTag(CbxScreenshotFormat);
        snap["ScreenshotPath"] = TxtScreenshotPath.Text;
        snap["EnableMaxSize"] = (ChkMaxSize.IsChecked == true).ToString().ToLower();
        snap["MaxScreenshotSizeMB"] = TxtMaxSize.Text;
        snap["EnableMaxAge"] = (ChkMaxAge.IsChecked == true).ToString().ToLower();
        snap["MaxScreenshotAgeDays"] = TxtMaxAge.Text;
        snap["DataRetentionDays"] = CbxDataRetention.Text ?? "";
        snap["EnableAI"] = (ChkEnableAI.IsChecked == true).ToString().ToLower();
        snap["AIMode"] = GetComboTag(CbxAIMode);
        snap["AIApiUrl"] = TxtApiUrl.Text;
        snap["AIApiKey"] = TxtApiKey.Password;
        snap["AIModel"] = TxtAIModel.Text;
        snap["AISummaryPath"] = TxtAISummaryPath.Text;
        snap["AISummaryMaxCount"] = TxtAISummaryMaxCount.Text;
        snap["AISummaryMaxSizeMB"] = TxtAISummaryMaxSizeMB.Text;
        snap["AutoStartWithWindows"] = (ChkAutoStart.IsChecked == true).ToString().ToLower();
        snap["MinimizeToTray"] = (ChkMinimizeToTray.IsChecked == true).ToString().ToLower();

        // 规则和分类数据
        snap["__rules"] = JsonSerializer.Serialize(_allRules.Select(r => new { r.ProcessName, r.TitleKeyword, r.CategoryName }).ToList());
        if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            snap["__categories"] = JsonSerializer.Serialize(cats.Select(c => new { c.Name, c.Color, c.SortOrder }).ToList());

        return snap;
    }

    /// <summary>
    /// 对比当前值和快照，检查是否有变更
    /// </summary>
    private void CheckHasChanges()
    {
        if (_loading || _originalSettings.Count == 0) return;
        var current = GetCurrentSettingsSnapshot();
        bool changed = false;
        foreach (var kvp in _originalSettings)
        {
            if (!current.TryGetValue(kvp.Key, out var val) || val != kvp.Value)
            {
                changed = true;
                break;
            }
        }
        _hasChanges = changed;
        TxtUnsaved.Text = changed ? "有未保存的更改" : "";
        BtnApply.IsEnabled = changed;
    }

    private static string GetComboTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
            return item.Tag.ToString() ?? "";
        return combo.Text ?? "";
    }

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

    private void RulesListView_Changed(object sender, RoutedEventArgs e)
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
        CheckHasChanges();
    }

    // ========== 备份数据库 ==========

    private void BtnBackupDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "SQLite 数据库|*.db|所有文件|*.*",
                FileName = $"timeactivity_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db"
            };
            if (dlg.ShowDialog() != true) return;

            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");
            if (!System.IO.File.Exists(dbPath))
            {
                MessageBox.Show("数据库文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 用 VACUUM INTO 备份，不需要停引擎
            DatabaseHelper.BackupTo(dlg.FileName);
            MessageBox.Show($"备份成功！\n{dlg.FileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("数据库备份失败", ex);
            MessageBox.Show($"备份失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            {"PollIntervalSeconds", "3"},
            {"IdleThresholdSeconds", "300"},
            {"AutoStartTracking", "true"},
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
            SettingsRepository.Set(kv.Key, kv.Value);

        _loading = true;
        LoadSettings();
        UpdateEstimates();
        UpdateDiskUsage();
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
            var settings = SettingsRepository.GetAll();
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
                SettingsRepository.Set(kv.Key, kv.Value);

            _loading = true;
            LoadSettings();
            LoadCategories();
            LoadRules();
            UpdateEstimates();
            UpdateDiskUsage();
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
}

