using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TimeActivity.Data;
using TimeActivity.Services;

namespace TimeActivity;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    // ========== 加载设置 ==========

    private void LoadSettings()
    {
        // 追踪设置
        SelectComboByTag(CbxSamplingInterval, DatabaseHelper.GetSetting("SamplingInterval", "3"));
        SelectComboByTag(CbxIdleThreshold, DatabaseHelper.GetSetting("IdleThreshold", "300"));
        ChkAutoStartTracking.IsChecked = DatabaseHelper.GetSetting("AutoStartTracking", "true") == "true";
        ChkTrackWindowTitle.IsChecked = DatabaseHelper.GetSetting("TrackWindowTitle", "true") == "true";

        // 截图设置
        ChkEnableScreenshot.IsChecked = DatabaseHelper.GetSetting("EnableScreenshot", "false") == "true";
        SelectComboByTag(CbxScreenshotInterval, DatabaseHelper.GetSetting("ScreenshotIntervalMinutes", "5"));
        SelectComboByTag(CbxScreenshotQuality, DatabaseHelper.GetSetting("ScreenshotQuality", "medium"));
        TxtScreenshotPath.Text = DatabaseHelper.GetSetting("ScreenshotPath",
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots"));

        // 显示设置
        Chk24Hour.IsChecked = DatabaseHelper.GetSetting("Use24Hour", "true") == "true";
        SelectComboByTag(CbxTheme, DatabaseHelper.GetSetting("Theme", "light"));

        // 数据设置
        SelectComboByTag(CbxDataRetention, DatabaseHelper.GetSetting("DataRetentionDays", "90"));

        // AI 设置
        ChkEnableAI.IsChecked = DatabaseHelper.GetSetting("EnableAI", "true") == "true";
        TxtApiUrl.Text = DatabaseHelper.GetSetting("AIApiUrl", "");
        TxtApiKey.Password = DatabaseHelper.GetSetting("AIApiKey", "");
        ChkAutoSummary.IsChecked = DatabaseHelper.GetSetting("AutoDailySummary", "true") == "true";

        // 系统设置
        ChkAutoStart.IsChecked = DatabaseHelper.GetSetting("AutoStartWithWindows", "false") == "true";
        ChkMinimizeToTray.IsChecked = DatabaseHelper.GetSetting("MinimizeToTray", "true") == "true";
        TxtHotkey.Text = DatabaseHelper.GetSetting("HotkeyToggleTracking", "Ctrl+Shift+T");
    }

    // ========== 保存设置 ==========

    public static event Action? SettingsSaved;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 追踪设置
        DatabaseHelper.SetSetting("SamplingInterval", GetComboTag(CbxSamplingInterval));
        DatabaseHelper.SetSetting("IdleThreshold", GetComboTag(CbxIdleThreshold));
        DatabaseHelper.SetSetting("AutoStartTracking", ChkAutoStartTracking.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("TrackWindowTitle", ChkTrackWindowTitle.IsChecked == true ? "true" : "false");

        // 截图设置
        DatabaseHelper.SetSetting("EnableScreenshot", ChkEnableScreenshot.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("ScreenshotIntervalMinutes", GetComboTag(CbxScreenshotInterval));
        DatabaseHelper.SetSetting("ScreenshotQuality", GetComboTag(CbxScreenshotQuality));
        DatabaseHelper.SetSetting("ScreenshotPath", TxtScreenshotPath.Text);

        // 显示设置
        DatabaseHelper.SetSetting("Use24Hour", Chk24Hour.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("Theme", GetComboTag(CbxTheme));

        // 数据设置
        DatabaseHelper.SetSetting("DataRetentionDays", GetComboTag(CbxDataRetention));

        // AI 设置
        DatabaseHelper.SetSetting("EnableAI", ChkEnableAI.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("AIApiUrl", TxtApiUrl.Text);
        DatabaseHelper.SetSetting("AIApiKey", TxtApiKey.Password);
        DatabaseHelper.SetSetting("AutoDailySummary", ChkAutoSummary.IsChecked == true ? "true" : "false");

        // 系统设置
        DatabaseHelper.SetSetting("AutoStartWithWindows", ChkAutoStart.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("MinimizeToTray", ChkMinimizeToTray.IsChecked == true ? "true" : "false");
        DatabaseHelper.SetSetting("HotkeyToggleTracking", TxtHotkey.Text);

        // 开机自启写快捷方式
        SetAutoStart(ChkAutoStart.IsChecked == true);

        // 通知主窗口重启服务
        SettingsSaved?.Invoke();

        MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings(); // 重新加载，放弃修改
    }

    // ========== 截图路径浏览 ==========

    private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
    {
        string selectedPath = BrowseFolder(TxtScreenshotPath.Text);
        if (!string.IsNullOrEmpty(selectedPath))
            TxtScreenshotPath.Text = selectedPath;
    }

    // Win32 FolderBrowserDialog (不引 WinForms)
    // 用 OpenFileDialog 的兼容模式选文件夹（最简单可靠）
    private static string BrowseFolder(string initialPath)
    {
        // 用 Microsoft.Win32 的文件夹选择（.NET 8 内置）
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存路径",
            InitialDirectory = initialPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
            return dialog.FolderName;
        return "";
    }

    // ========== 清空数据 ==========

    private void BtnClearData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要清空所有活动记录、截图和统计数据吗？\n此操作不可恢复！",
            "警告",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var result2 = MessageBox.Show(
            "再次确认：真的要删除所有数据吗？",
            "再次确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result2 != MessageBoxResult.Yes) return;

        DatabaseHelper.ClearAllData();
        MessageBox.Show("所有数据已清空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ========== 开机自启（启动文件夹快捷方式） ==========

    private void SetAutoStart(bool enable)
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = System.IO.Path.Combine(startupFolder, "TimeActivity.lnk");

            if (enable)
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                CreateShortcut(shortcutPath, exePath, "--minimized");
            }
            else
            {
                if (System.IO.File.Exists(shortcutPath))
                    System.IO.File.Delete(shortcutPath);
            }
        }
        catch { }
    }

    // 用 WScript.Shell COM 创建快捷方式
    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(targetPath);
        shortcut.WindowStyle = 1;
        shortcut.Save();
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
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string GetComboTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "";
        return "";
    }
}
