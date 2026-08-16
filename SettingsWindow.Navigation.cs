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
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList == null || PanelTracking == null) return;

        // 先全部隐藏
        PanelTracking.Visibility = Visibility.Collapsed;
        PanelScreenshot.Visibility = Visibility.Collapsed;
        PanelRules.Visibility = Visibility.Collapsed;
        PanelCategories.Visibility = Visibility.Collapsed;
        PanelData.Visibility = Visibility.Collapsed;
        PanelAI.Visibility = Visibility.Collapsed;
        PanelSystem.Visibility = Visibility.Collapsed;
        PanelIO.Visibility = Visibility.Collapsed;

        // 根据选中索引显示对应面板(显示设置已删除,编号调整)
        switch (NavList.SelectedIndex)
        {
            case 0: PanelTracking.Visibility = Visibility.Visible; break;
            case 1: PanelScreenshot.Visibility = Visibility.Visible; break;
            case 2: PanelRules.Visibility = Visibility.Visible;
                    // 限制 PanelRules 高度让左右独立滚动
                    PanelRules.MaxHeight = Math.Max(300, SettingsScroll.ViewportHeight - 50);
                    // 延迟加载:首次切到分类规则页才加载规则
                    if (_allRules.Count == 0 && !_rulesLoaded)
                    {
                        _rulesLoaded = true;
                        LoadRules();
                    }
                    break;
            case 3: PanelCategories.Visibility = Visibility.Visible; break;
            case 4: PanelData.Visibility = Visibility.Visible; break;
            case 5: PanelAI.Visibility = Visibility.Visible; break;
            case 6: PanelSystem.Visibility = Visibility.Visible; break;
            case 7: PanelIO.Visibility = Visibility.Visible; break;
        }
    }

    private void LoadSettings()
    {
        // 追踪设置
        SetComboByTagOrText(CbxSamplingInterval, SettingsRepository.Get("PollIntervalSeconds", "3"), "秒");
        SetComboByTagOrText(CbxIdleThreshold, SettingsRepository.Get("IdleThresholdSeconds", "300"), "分钟");
        // 如果 Tag 匹配不上(自定义值),把秒转回分钟显示
        if (CbxIdleThreshold.SelectedIndex == -1)
        {
            if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idleSec))
                CbxIdleThreshold.Text = (idleSec / 60).ToString();
        }
        ChkAutoStartTracking.IsChecked = SettingsRepository.Get("AutoStartTracking", "true") == "true";

        // 截图设置
        ChkEnableScreenshot.IsChecked = SettingsRepository.Get("EnableScreenshot", "false") == "true";
        ScreenshotOptionsPanel.IsEnabled = ChkEnableScreenshot.IsChecked == true;
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

        // PNG 格式时隐藏质量选项(PNG 无损,不涉及压缩质量)
        QualityRow.Visibility = fmt == "png" ? Visibility.Collapsed : Visibility.Visible;

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

            // 用 VACUUM INTO 备份,不需要停引擎
            DatabaseHelper.BackupTo(dlg.FileName);
            MessageBox.Show($"备份成功!\n{dlg.FileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("数据库备份失败", ex);
            MessageBox.Show($"备份失败:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClearData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要清空所有活动记录、截图和统计数据吗?\n此操作不可恢复!",
            "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var result2 = MessageBox.Show(
            "再次确认:真的要删除所有数据吗?",
            "再次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result2 != MessageBoxResult.Yes) return;

        DatabaseHelper.ClearAllData();
        UpdateDiskUsage();
        MessageBox.Show("所有数据已清空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

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
            MessageBox.Show($"导出失败:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_rulesLoaded) LoadRules();
            UpdateEstimates();
            UpdateDiskUsage();
            _loading = false;
            _hasChanges = false;
            TxtUnsaved.Text = "";
            MessageBox.Show("设置已导入", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
        // 匹配不上:直接写文本(可编辑模式)
        combo.Text = value;
    }

}
