using System;
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

public partial class SettingsWindow : Window
{
    private bool _loading = false;
    private bool _hasChanges = false;
    private Dictionary<string, string> _originalSettings = new();

    // 分类列表(用于规则下拉和分类管理)
    private List<CategoryItem> _categories = new();

    // 分类名列表(供 DataGridComboBoxColumn 绑定用)
    public List<string> _categoryNames = new();

    public SettingsWindow(string initialSection = null)
    {
        InitializeComponent();
        try
        {
            _loading = true;
            LoadSettings();
            LoadCategories();
            // LoadRules 延迟到用户切到分类规则页才加载
        }
        finally
        {
            _loading = false;
        }
        _hasChanges = false;
        SaveSnapshot(); // 记录初始快照,BtnApply 才能正常启用
        // 耗时操作异步执行
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateEstimates();
            UpdateDiskUsage();
        }), System.Windows.Threading.DispatcherPriority.Background);

        // 根据初始参数选中对应导航项
        if (!string.IsNullOrEmpty(initialSection))
        {
            int index = initialSection.ToLower() switch
            {
                "tracking" => 0,
                "screenshot" => 1,
                "rules" => 2,
                "categories" => 3,
                "data" => 4,
                "ai" => 5,
                "system" => 6,
                "io" => 7,
                _ => -1
            };
            if (index >= 0 && NavList != null)
            {
                NavList.SelectedIndex = index;
            }
        }
    }

    // ========== 侧边栏导航 ==========


    // ========== 加载设置 ==========


    // ========== 分类管理 ==========



    // ========== 分类规则(折叠面板版) ==========

    // 所有规则项(内存缓存)
    private List<RuleItem> _allRules = new();

    // 规则是否已加载(延迟加载,首次切到分类规则页才加载)
    private bool _rulesLoaded = false;

    // 多选的进程名集合
    private HashSet<string> _selectedProcessNames = new();

    // Shift 范围选择用的上次点击进程名
    private string? _lastClickedProcess = null;






    // ========== 多选逻辑 ==========






    // ========== 搜索 ==========

    // 搜索防抖定时器
    private DispatcherTimer? _searchDebounceTimer;



    // ========== 左侧分类列表交互 ==========


    // ========== 拖拽到左侧分类 ==========







    // ========== 保存设置 ==========

    /// <summary>设置保存后的事件通知(主窗口订阅后重启服务、重载规则等)</summary>
    public static event Action? SettingsSaved;




    // ========== 删行按钮 + 颜色选择器 ==========

    // BtnDeleteRule_Click 已移除(新方案无删除按钮,改分类用拖拽)











    // ========== AI 总结路径浏览 ==========


    // ========== AI 测试连接 ==========


    // ========== 预估占用大小 ==========





    // ========== AI 模式切换 ==========


    // ========== 事件 ==========






    // ========== 未保存提示 ==========


    // ========== 备份数据库 ==========


    // ========== 清空数据 ==========


    // ========== 恢复此页默认 ==========


    // ========== 导入导出 ==========



    // ========== 辅助方法 ==========


}

