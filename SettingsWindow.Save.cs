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
    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DoSave();
        MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        this.Close();
    }

    private void SaveCategories()
    {
        try
        {
            if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            {
                // 收集当前 UI 中存在的分类 Id
                var currentIds = new HashSet<int>();
                foreach (var c in cats)
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    currentIds.Add(c.Id);
                    // Id<=0 表示新分类,插入后获新 Id 并回写到 UI 对象
                    // Id>0 更新已有分类
                    int newId = CategoryRepository.UpdateOrInsert(c.Id, c.Name, c.Color ?? "#808080", c.SortOrder);
                    if (c.Id <= 0) c.Id = newId;
                }

                // 删除用户在 UI 中删掉的自定义分类(预置 Id<=13 不可删)
                var dbCats = CategoryRepository.GetAll();
                foreach (var dbCat in dbCats)
                {
                    if (dbCat.Id > CategoryRepository.MaxPresetCategoryId && !currentIds.Contains(dbCat.Id))
                    {
                        CategoryRepository.Delete(dbCat.Id);
                    }
                }
            }
        }
        catch (Exception ex) { Logger.Error("SaveCategories 失败", ex); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // 不保存直接关窗
        this.Close();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        // 和保存一样逻辑,但不关窗
        DoSave();
    }

    private void DoSave()
    {
        // 追踪设置:采样间隔和空闲阈值
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

        // 截图设置:开关、间隔、格式、质量、路径、存储限制
        SettingsRepository.Set("EnableScreenshot", ChkEnableScreenshot.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("ScreenshotOnSwitch", ChkScreenshotOnSwitch.IsChecked == true ? "true" : "false");

        // 截图间隔
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

        // AI 总结文件保存
        SettingsRepository.Set("AISummaryPath", TxtAISummaryPath.Text);
        SettingsRepository.Set("AISummaryMaxCount", TxtAISummaryMaxCount.Text);
        SettingsRepository.Set("AISummaryMaxSizeMB", TxtAISummaryMaxSizeMB.Text);

        // 系统设置
        SettingsRepository.Set("AutoStartWithWindows", ChkAutoStart.IsChecked == true ? "true" : "false");
        SettingsRepository.Set("MinimizeToTray", ChkMinimizeToTray.IsChecked == true ? "true" : "false");

        // 开机自启设置
        if (ChkAutoStart.IsChecked == true)
            AutoStartHelper.Enable();
        else
            AutoStartHelper.Disable();

        // 保存分类规则和分类管理
        SaveRules();
        SaveCategories();

        // 刷新侧边栏 Count
        LoadCategorySidebar();

        // 通知主窗口重启服务 + 重新分类历史数据
        SettingsSaved?.Invoke();

        // 刷新预估值和磁盘占用
        UpdateEstimates();
        UpdateDiskUsage();

        _hasChanges = false;
        TxtUnsaved.Text = "";
        BtnApply.IsEnabled = false;
        SaveSnapshot();
    }

    private void SaveSnapshot()
    {
        _originalSettings = GetCurrentSettingsSnapshot();
    }

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

    private void BtnRestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        string pageName = NavList.SelectedIndex switch
        {
            0 => "追踪设置",
            1 => "截图设置",
            2 => "分类规则",
            3 => "分类管理",
            4 => "数据设置",
            5 => "AI 设置",
            6 => "系统设置",
            7 => "导入/导出",
            _ => "当前页"
        };

        // 分类规则页恢复默认会清空用户自定义规则,需额外提醒
        string hint = NavList.SelectedIndex switch
        {
            2 => "恢复「分类规则」到默认值将清空所有自定义分类映射,此操作不可撤销。\n\n确定继续?",
            3 => "恢复「分类管理」将删除所有自定义分类并重置预置分类颜色。\n\n确定继续?",
            _ => $"恢复「{pageName}」到默认值并保存?"
        };

        var result = MessageBox.Show(hint, "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        switch (NavList.SelectedIndex)
        {
            case 0: // 追踪设置
            case 1: // 截图设置
            case 4: // 数据设置
            case 5: // AI 设置
            case 6: // 系统设置
                foreach (var kv in SettingsRepository.GetDefaultsByPage(NavList.SelectedIndex))
                    SettingsRepository.Set(kv.Key, kv.Value);
                break;

            case 2: // 分类规则
                RuleRepository.ClearAll();
                _rulesLoaded = false;
                _allRules.Clear();
                if (PanelRules.Visibility == Visibility.Visible)
                {
                    _rulesLoaded = true;
                    LoadRules();
                }
                break;

            case 3: // 分类管理
                CategoryRepository.ResetToDefault();
                LoadCategories();
                break;

            case 7: // 导入/导出
                MessageBox.Show("导入/导出页无需恢复默认", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
        }

        _loading = true;
        LoadSettings();
        if (_rulesLoaded && NavList.SelectedIndex == 2) LoadRules();
        if (NavList.SelectedIndex == 3) LoadCategories();
        UpdateEstimates();
        UpdateDiskUsage();
        _loading = false;

        // 直接保存(不关窗)
        DoSave();
        TxtUnsaved.Text = "✓ 已恢复默认并保存";
    }

}
