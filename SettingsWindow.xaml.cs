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

// ============================================================================
// SettingsWindow.xaml.cs — 设置窗口主文件（partial 类的核心）
// 职责：
//   1) 持有窗口级共享状态：装载标志、脏标记、原始快照、分类缓存、规则缓存等；
//   2) 构造流程：装载设置与分类 → 记录快照 → 后台刷新估算 → 定位初始导航页；
//   3) 声明 SettingsSaved 静态事件，供主窗口订阅以应用新配置。
// 其余逻辑分布在各部分类：
//   Navigation(导航/装载/备份/导入导出)、Appearance(AI/截图/颜色)、
//   Categories(分类管理/规则面板)、Rules(规则加载保存)、Save(保存/恢复默认)。
// ============================================================================
public partial class SettingsWindow : Window
{
    // 装载标志：true 表示正在程序化回填控件，此时抑制一切联动事件
    private bool _loading = false;
    // 脏标记：界面值相对上次保存是否发生变化
    private bool _hasChanges = false;
    // 上次保存时的设置快照（键=设置名或 __rules/__categories，值=文本/JSON）
    private Dictionary<string, string> _originalSettings = new();

    // 分类列表(用于规则下拉和分类管理)
    private List<CategoryItem> _categories = new();

    // 分类名列表(供 DataGridComboBoxColumn 绑定用)
    public List<string> _categoryNames = new();

    /// <summary>
    /// 构造函数：装载设置与分类、记录初始快照、后台刷新占用估算，
    /// 并按 initialSection 参数定位到指定导航页（供主窗口跳转用）。
    /// </summary>
    /// <param name="initialSection">初始分区名，如 "rules"；为空则停在默认页</param>
    public SettingsWindow(string initialSection = null)
    {
        InitializeComponent(); // 初始化 XAML 控件
        try
        {
            _loading = true;   // 装载期：抑制控件联动事件
            LoadSettings();    // 设置表 → 控件
            LoadCategories();  // 分类表 → 网格/缓存
            // LoadRules 延迟到用户切到分类规则页才加载
        }
        finally
        {
            _loading = false;  // 无论成败都退出装载态
        }
        _hasChanges = false;   // 初始无更改
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
            // 分区名 → 导航索引（与 Navigation 部分类中的面板顺序一一对应）
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
            if (index >= 0 && NavList != null) // 合法索引才切换
            {
                NavList.SelectedIndex = index; // 触发 SelectionChanged 显示对应面板
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

    /// <summary>设置保存后的事件通知(主窗口订阅后重启服务、重载规则等)。
    /// 静态事件：即使窗口关闭重建，订阅关系依然有效。</summary>
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

