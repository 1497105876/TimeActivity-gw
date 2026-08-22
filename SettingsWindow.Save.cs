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
// SettingsWindow.Save.cs — 设置窗口的"保存/恢复默认/更改检测"部分类
// 职责：
//   1) DoSave：把全部界面控件值写回设置表，并保存分类规则与分类管理；
//   2) SaveCategories：UI 分类集合与数据库对齐（新增/更新/删除自定义分类）；
//   3) 快照机制：保存时记录当前值快照，任何控件变化后与快照比对显示"未保存"提示；
//   4) BtnRestoreDefault：按页恢复默认值（分类规则/分类管理有额外危险确认）。
// 协作对象：SettingsRepository、CategoryRepository/RuleRepository、
//           AutoStartHelper(开机自启)、SettingsSaved 事件(通知主窗口)。
// ============================================================================
public partial class SettingsWindow
{
    /// <summary>保存按钮：执行保存 → 提示 → 关闭窗口。</summary>
    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DoSave(); // 写库
        MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        this.Close(); // 保存后关闭窗口
    }

    /// <summary>
    /// 把分类网格中的数据与数据库对齐：
    /// 网格中存在的 → 新增(Id≤0)或更新；数据库有而网格没有的自定义分类 → 删除。
    /// </summary>
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

    /// <summary>取消按钮：不做任何保存直接关闭窗口。</summary>
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // 不保存直接关窗
        this.Close();
    }

    /// <summary>应用按钮：与保存相同逻辑，但不关闭窗口（方便继续调整）。</summary>
    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        // 和保存一样逻辑,但不关窗
        DoSave();
    }

    /// <summary>
    /// 核心保存流程：读取每个控件 → 解析/校验 → 写入设置表；
    /// 随后保存规则与分类、通知主窗口应用新配置、刷新估算并更新快照。
    /// </summary>
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

        _hasChanges = false;   // 保存后清除脏标记
        TxtUnsaved.Text = "";  // 清空提示
        BtnApply.IsEnabled = false; // 无更改时"应用"置灰
        SaveSnapshot();        // 以当前值作为新的对比基准
    }

    /// <summary>保存当前界面值为快照，供后续变更检测比对。</summary>
    private void SaveSnapshot()
    {
        _originalSettings = GetCurrentSettingsSnapshot();
    }

    /// <summary>
    /// 从界面控件收集全部设置项的当前值（含规则/分类的 JSON 序列化），
    /// 作为变更检测的快照数据。
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
        snap["__rules"] = JsonSerializer.Serialize(_allRules.Select(r => new { r.ProcessName, r.TitleKeyword, r.CategoryName }).ToList()); // 规则集合序列化
        if (CategoriesGrid.ItemsSource is ObservableCollection<CategoryItem> cats)
            snap["__categories"] = JsonSerializer.Serialize(cats.Select(c => new { c.Name, c.Color, c.SortOrder }).ToList()); // 分类集合序列化

        return snap;
    }

    /// <summary>
    /// 变更检测：当前快照与保存时快照逐键比对，
    /// 有差异则显示"有未保存的更改"并启用"应用"按钮。
    /// </summary>
    private void CheckHasChanges()
    {
        if (_loading || _originalSettings.Count == 0) return; // 装载期/无基准快照时不判定
        var current = GetCurrentSettingsSnapshot();
        bool changed = false;
        foreach (var kvp in _originalSettings)
        {
            if (!current.TryGetValue(kvp.Key, out var val) || val != kvp.Value) // 任一键不同即为有更改
            {
                changed = true;
                break;
            }
        }
        _hasChanges = changed;                          // 记录脏状态
        TxtUnsaved.Text = changed ? "有未保存的更改" : ""; // 界面提示
        BtnApply.IsEnabled = changed;                   // 应用按钮随状态启停
    }

    /// <summary>
    /// "恢复默认"按钮：按当前所在页恢复默认值并立即保存。
    /// 分类规则页会清空全部规则映射、分类管理页会删除自定义分类，均需强提醒。
    /// </summary>
    private void BtnRestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        // 根据导航索引得到页面名（用于确认文案）
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
                foreach (var kv in SettingsRepository.GetDefaultsByPage(NavList.SelectedIndex)) // 按页取默认键值对逐项写回
                    SettingsRepository.Set(kv.Key, kv.Value);
                break;

            case 2: // 分类规则
                RuleRepository.ClearAll();   // 清空全部规则
                _rulesLoaded = false;        // 复位加载标志
                _allRules.Clear();           // 清空内存规则
                if (PanelRules.Visibility == Visibility.Visible) // 面板可见则立即重载展示"未分类"占位
                {
                    _rulesLoaded = true;
                    LoadRules();
                }
                break;

            case 3: // 分类管理
                CategoryRepository.ResetToDefault(); // 删除自定义分类并重置预置色
                LoadCategories();
                break;

            case 7: // 导入/导出
                MessageBox.Show("导入/导出页无需恢复默认", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return; // 该页无默认值，直接返回不执行后续保存
        }

        _loading = true;          // 装载期抑制联动事件
        LoadSettings();           // 重载设置到控件
        if (_rulesLoaded && NavList.SelectedIndex == 2) LoadRules();   // 规则页同步重载
        if (NavList.SelectedIndex == 3) LoadCategories();              // 分类页同步重载
        UpdateEstimates();
        UpdateDiskUsage();
        _loading = false;

        // 直接保存(不关窗)
        DoSave();
        TxtUnsaved.Text = "✓ 已恢复默认并保存";
    }

}
