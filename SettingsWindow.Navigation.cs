// 引用的命名空间（与各部分类文件保持一致的 using 集）
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
// SettingsWindow.Navigation.cs — 设置窗口的"导航与设置装载/导入导出"部分类
// 职责：
//   1) 左侧导航列表切换：显示对应面板，规则页首次进入时延迟加载；
//   2) LoadSettings：把数据库中的全部设置项回填到各控件；
//   3) 截图目录/AI 总结目录选择、数据库备份、清空数据（双重确认）；
//   4) 设置的 JSON 导出/导入（导入后全界面重载）；
//   5) ComboBox 按 Tag/文本匹配选中小工具方法。
// 协作对象：SettingsRepository(设置读写)、DatabaseHelper(备份/清空)、
//           LoadRules/LoadCategories/UpdateEstimates(其他部分类)。
// ============================================================================
public partial class SettingsWindow
{
    /// <summary>
    /// 导航列表选中项变化：隐藏全部面板后按索引显示对应分区。
    /// 索引顺序：0追踪 1截图 2分类规则 3分类管理 4数据 5AI 6系统 7导入导出。
    /// </summary>
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList == null || PanelTracking == null) return; // XAML 未就绪时忽略

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
                        _rulesLoaded = true; // 置标志防止重复加载
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

    /// <summary>
    /// 装载全部设置到界面控件：读取 SettingsRepository 的每一项，
    /// 回填下拉框/复选框/文本框；装载期间 _loading=true 可抑制联动事件。
    /// </summary>
    private void LoadSettings()
    {
        // 追踪设置
        // 采样间隔下拉：库中键 PollIntervalSeconds(秒)，默认 3；下拉可编辑，手输的自定义值靠"文本直接写入"兜底
        SetComboByTagOrText(CbxSamplingInterval, SettingsRepository.Get("PollIntervalSeconds", "3"), "秒");
        // 空闲阈值下拉：显示用分钟，库中键 IdleThresholdSeconds 以秒存(默认 300 秒=5 分钟)，Tag 里放秒值才能直接命中
        SetComboByTagOrText(CbxIdleThreshold, SettingsRepository.Get("IdleThresholdSeconds", "300"), "分钟");
        // 如果 Tag 匹配不上(自定义值),把秒转回分钟显示
        // SelectedIndex==-1 说明库里存的是标准选项之外的秒值（比如手输过 8 分钟 → 存了 480 秒）
        if (CbxIdleThreshold.SelectedIndex == -1)
        {
            // 解析出来的是秒，下拉只显示分钟 → 除以 60 回填，保证文字可读
            if (int.TryParse(SettingsRepository.Get("IdleThresholdSeconds", "300"), out int idleSec))
                CbxIdleThreshold.Text = (idleSec / 60).ToString();
        }
        ChkAutoStartTracking.IsChecked = SettingsRepository.Get("AutoStartTracking", "true") == "true"; // 字符串比较解析布尔

        // 截图设置
        // 截图总开关默认关(存 "false")：毕竟截图涉及隐私，不默认开启
        ChkEnableScreenshot.IsChecked = SettingsRepository.Get("EnableScreenshot", "false") == "true";
        ScreenshotOptionsPanel.IsEnabled = ChkEnableScreenshot.IsChecked == true; // 选项面板可用性跟随开关
        ChkScreenshotOnSwitch.IsChecked = SettingsRepository.Get("ScreenshotOnSwitch", "true") == "true"; // 切换截屏默认开

        // 定时截图间隔(分钟)：Tag 存的就是分钟数本身，可直接命中选项
        string intervalStr = SettingsRepository.Get("ScreenshotIntervalMinutes", "5");
        SetComboByTagOrText(CbxScreenshotInterval, intervalStr, "分钟");

        // 截图格式
        string fmt = SettingsRepository.Get("ScreenshotFormat", "jpg");
        foreach (ComboBoxItem item in CbxScreenshotFormat.Items)
        {
            if (item.Tag?.ToString() == fmt) { CbxScreenshotFormat.SelectedItem = item; break; } // 按 Tag 定位格式项
        }
        // 二次兜底：无论上面是否命中都按 fmt 强制定位，杜绝残留脏状态
        CbxScreenshotFormat.SelectedIndex = fmt == "png" ? 1 : 0;

        // PNG 格式时隐藏质量选项(PNG 无损,不涉及压缩质量)
        QualityRow.Visibility = fmt == "png" ? Visibility.Collapsed : Visibility.Visible;

        SelectComboByTag(CbxScreenshotQuality, SettingsRepository.Get("ScreenshotQuality", "medium")); // 质量档:high/medium/low，仅 JPEG 生效
        TxtScreenshotPath.Text = SettingsRepository.Get("ScreenshotPath",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots")); // 默认存到程序目录下的 screenshots

        // 存储限制：容量上限与保留天数两套独立开关，各自带一个数值输入框
        ChkMaxSize.IsChecked = SettingsRepository.Get("EnableMaxSize", "true") == "true";
        TxtMaxSize.Text = SettingsRepository.Get("MaxScreenshotSizeMB", "5120");
        ChkMaxAge.IsChecked = SettingsRepository.Get("EnableMaxAge", "true") == "true";
        TxtMaxAge.Text = SettingsRepository.Get("MaxScreenshotAgeDays", "30");

        // 数据设置
        // 保留天数下拉：Tag 存天数，默认 90；选项中"永久"对应 0，即永不清历史数据
        SetComboByTagOrText(CbxDataRetention, SettingsRepository.Get("DataRetentionDays", "90"), "天");

        // AI 设置（2026-08-23 重做：服务商预设 + OpenAI 兼容统一接口）
        ChkEnableAI.IsChecked = SettingsRepository.Get("EnableAI", "true") == "true"; // AI 总开关
        // 兼容迁移：老版本只有 AIMode(lan/custom)。判定顺序：
        //   ① 已有 AIProvider → 直接用；
        //   ② 有旧 AIMode     → 映射(lan→ollama / custom→custom)并回写；
        //   ③ 全新安装        → 默认 custom（2026-08-23：不再预置本地 Ollama 与模型名）。
        string provider = SettingsRepository.Get("AIProvider", "");
        if (string.IsNullOrEmpty(provider))
        {
            var legacyMode = SettingsRepository.Get("AIMode", "");
            provider = legacyMode == "" ? "custom"
                     : (legacyMode == "lan" ? "ollama" : "custom");
            // 迁移判定结果立即回写，避免下次启动重复判定
            SettingsRepository.Set("AIProvider", provider);
        }
        // 按 Tag 选中服务商下拉项
        foreach (ComboBoxItem item in CbxAIProvider.Items)
        {
            if (item.Tag?.ToString() == provider) { CbxAIProvider.SelectedItem = item; break; }
        }
        _currentAiProviderTag = provider; // 记忆切换的初始锚点（装载期不触发联动）
        // 服务商三要素直接回填：地址/Key/模型名（Key 存在库里，装载后塞进 PasswordBox 保持密文显示）
        TxtApiUrl.Text = SettingsRepository.Get("AIApiUrl", "");
        TxtApiKey.Password = SettingsRepository.Get("AIApiKey", "");
        CbxAIModel.Text = SettingsRepository.Get("AIModel", "");

        // AI 高级参数（留空=用服务商默认值）
        TxtAITemperature.Text = SettingsRepository.Get("AITemperature", "");   // 采样温度，字符串传空表示不动它
        TxtAIMaxTokens.Text = SettingsRepository.Get("AIMaxTokens", "");       // 单次回复 token 上限
        TxtAITimeout.Text = SettingsRepository.Get("AITimeoutSeconds", "");    // 请求超时(秒)

        // AI 总结文件保存设置：导出目录 + 保留个数/总大小两个阈值，具体口径由 AISummaryService 消费
        TxtAISummaryPath.Text = SettingsRepository.Get("AISummaryPath", "");
        TxtAISummaryMaxCount.Text = SettingsRepository.Get("AISummaryMaxCount", "0");
        TxtAISummaryMaxSizeMB.Text = SettingsRepository.Get("AISummaryMaxSizeMB", "0");

        // 系统设置
        // 开机自启默认关；这里只回填勾选，真正写注册表要等点保存时调用 AutoStartHelper
        ChkAutoStart.IsChecked = SettingsRepository.Get("AutoStartWithWindows", "false") == "true";
        // 最小化到托盘默认开：开启后最小化不会占据任务栏，而是藏到系统托盘图标里
        ChkMinimizeToTray.IsChecked = SettingsRepository.Get("MinimizeToTray", "true") == "true";
    }

    /// <summary>读取 ComboBox 当前值：优先取选中项 Tag，可编辑模式下退回文本。</summary>
    private static string GetComboTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
            return item.Tag.ToString() ?? "";
        return combo.Text ?? "";
    }

    /// <summary>"浏览…"按钮：选择截图保存目录并刷新磁盘占用显示。</summary>
    private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存路径",
            InitialDirectory = TxtScreenshotPath.Text ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) // 以当前值为初始目录
        };

        if (dialog.ShowDialog() == true) // 用户确认选择
        {
            TxtScreenshotPath.Text = dialog.FolderName; // 回填路径
            UpdateDiskUsage(); // 立即统计新目录占用
            MarkChanged();
        }
    }

    /// <summary>"浏览…"按钮：选择 AI 总结导出目录。</summary>
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

    /// <summary>
    /// "备份数据库"按钮：弹出保存对话框，用 SQLite 的 VACUUM INTO 在线备份
    /// （不需要停止追踪引擎），文件名默认带时间戳。
    /// </summary>
    private void BtnBackupDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "SQLite 数据库|*.db|所有文件|*.*",
                FileName = $"timeactivity_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db" // 默认文件名含时间戳
            };
            if (dlg.ShowDialog() != true) return; // 用户取消

            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timeactivity.db");
            if (!System.IO.File.Exists(dbPath)) // 库文件不存在（异常状态）直接提示
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

    // 「清空所有数据」入口已按需求移除（2026-08-23）：
    // 数据安全仍由「按保留天数自动清理(CleanOldData)」与「手动备份(BackupTo)」保障。
    // 历史实现 BtnClearData_Click 见版本库。

    /// <summary>
    /// "导出设置"按钮：把全部设置项序列化为缩进 JSON 保存到用户指定文件。
    /// </summary>
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
            var settings = SettingsRepository.GetAll(); // 读取全部键值对
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }); // 缩进格式便于阅读
            File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
            MessageBox.Show($"设置已导出到\n{dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// "导入设置"按钮：读取 JSON 键值对逐项写入数据库，
    /// 然后整体重载界面（装载期置 _loading 抑制联动），最后清除脏标记。
    /// </summary>
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
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json); // 反序列化为键值对
            if (settings == null) { MessageBox.Show("文件内容为空", "错误"); return; }

            foreach (var kv in settings)
                SettingsRepository.Set(kv.Key, kv.Value); // 逐项落库

            _loading = true;      // 装载期抑制控件联动事件
            LoadSettings();       // 重载设置到界面
            LoadCategories();     // 重载分类
            if (_rulesLoaded) LoadRules(); // 规则页若已加载过则同步重载
            UpdateEstimates();    // 重算截图占用估算
            UpdateDiskUsage();    // 重算磁盘占用
            _loading = false;
            _hasChanges = false;  // 导入即保存，清除脏标记
            TxtUnsaved.Text = ""; // 清空未保存提示文字
            MessageBox.Show("设置已导入", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 按 Tag 精确匹配选中 ComboBox 项；全部不中则选中第一项兜底。
    /// </summary>
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
        if (combo.Items.Count > 0) combo.SelectedIndex = 0; // 兜底选第一项
    }

    /// <summary>
    /// 按 Tag 或"去掉单位后缀的文本"匹配选中项；都匹配不上时把值直接写入文本（可编辑下拉）。
    /// 用于回填自定义值（如用户手输的间隔秒数）。
    /// </summary>
    private static void SetComboByTagOrText(ComboBox combo, string value, string suffix = "")
    {
        foreach (ComboBoxItem item in combo.Items) // 第一轮：Tag 精确匹配
        {
            if (item.Tag?.ToString() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        foreach (ComboBoxItem item in combo.Items) // 第二轮：文本去单位后匹配
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
