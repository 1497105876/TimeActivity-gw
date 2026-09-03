// ============================================================================
// using 指令区
// ============================================================================
// .NET 基础类型与核心功能（DateTime、Math、String 等）
using System;
// 泛型集合容器（Dictionary<TKey,TValue> 等）
using System.Collections.Generic;
// 区域文化设置（InvariantCulture 用于解析数据库中的时间字符串）
using System.Globalization;
// LINQ 查询扩展（Where / Sum / GroupBy / ToDictionary 等）
using System.Linq;
// WPF 基础类型（Visibility、RoutedEventArgs、SizeChangedEventArgs 等）
using System.Windows;
// WPF 控件库（Page、RadioButton、ComboBoxItem 等）
using System.Windows.Controls;
// 画刷与颜色类型（Brushes/Color，图表配色辅助）
using System.Windows.Media;
// Shape 图元（Line/Ellipse 等，ChartRenderer 绘制折线与圆点所依赖）
using System.Windows.Shapes;
// 项目内：数据仓储层（活动记录 / 每日汇总 / 分类 / AI 总结）
using TimeActivity.Data;
// 项目内：服务层（AI 总结生成与文件保存）
using TimeActivity.Services;
// 项目内：辅助层（分类颜色加载、周起始日计算）
using TimeActivity.Helpers;
// 项目内：渲染层（趋势折线 / 占比条 / Top 应用绘制器）
using TimeActivity.Rendering;

// 文件级命名空间声明（C# 10+ 单行写法，全文件共用一个命名空间）
namespace TimeActivity;

/// <summary>
/// 统计报表页 — 提供日/周/月三个维度的活动数据可视化，包括类别占比、
/// 每日趋势折线图、Top 应用排行，以及 AI 总结生成。
/// </summary>
// ============================================================================
// StatisticsPage.xaml.cs — 统计报表页（嵌入主窗口 Tab 的 Frame 中）
// 职责：
//   1) 日/周/月三档周期切换与前后翻页，计算对应日期范围；
//   2) 类别占比、每日趋势折线图、Top 应用排行的数据聚合与渲染调度；
//   3) 分类筛选：下拉框单选分类后按新条件重新聚合（选中具体分类时，日模式的占比栏整段隐藏）；
//   4) AI 总结的展示/手动生成（当前期查 manual；历史日期回退 auto）；
//   5) 「跳过空闲」开关：日模式对所有明细聚合生效；周/月模式因预汇总表建表时已剔除空闲，只影响趋势图取哪一列。
// 协作对象：ActivityRepository/DailySummaryRepository(聚合：日模式扫明细表、周/月读每日预汇总表)、
//           AISummaryRepository/AISummaryService(AI 总结)、ChartRenderer(占比条/趋势折线/Top 三张图)、
//           CategoryColorHelper(分类配色)、CategoryRepository(下拉数据源)、DateHelper(周起始边界)。
// ============================================================================
public partial class StatisticsPage : Page
{
    // 当前查看的周期模式：day / week / month
    private string _period = "day";

    // 当前周期的参考日期（日模式=当天，周模式=该周任意一天，月模式=该月任意一天）
    private DateTime _periodStart = DateTime.Today;

    // 分类颜色助手与图表渲染器
    private CategoryColorHelper _colorHelper = new(); // 从 Categories 表读取“分类→颜色”，图表取色统一走它
    private ChartRenderer _chartRenderer;             // 负责占比条 / 趋势折线 / Top 应用的绘制

    // 分类名 → 颜色十六进制字符串
    private Dictionary<string, string> _categoryColors = new(); // “分类名 → #RRGGBB”，Load() 一次建好后长期复用

    // 缓存趋势数据，窗口 SizeChanged 时重绘用
    private Dictionary<string, int> _cachedDailyData = new(); // 最近一次聚合的“日期(yyyy-MM-dd) → 当日秒数”
    private DateTime _cachedRangeStart;                       // 缓存趋势数据的起始日期
    private DateTime _cachedRangeEnd;                         // 缓存趋势数据的结束日期

    /// <summary>
    /// 构造函数：初始化颜色、图表渲染器，加载分类筛选和默认数据
    /// </summary>
    public StatisticsPage()
    {
        InitializeComponent();                    // 初始化 XAML
        _categoryColors = _colorHelper.Load();    // 预载分类颜色
        _chartRenderer = new ChartRenderer(_colorHelper); // 创建折线图渲染器
        LoadCategoryFilter();                     // 构建分类筛选复选框
        RbDay.IsChecked = true; // 默认勾选「日」，保证控件态与 _period="day" 一致（页面未加载，即便触发 Checked 事件也会被 IsLoaded 挡掉）
        UpdateRange();                            // 计算并显示日期范围 + 载入 AI 总结
        LoadData();                               // 加载图表与排行数据
    }

    /// <summary>
    /// 外部调用的刷新方法：重新加载颜色和当前周期数据（设置保存后用）
    /// </summary>
    public void RefreshData()
    {
        // 重建颜色助手确保不残留旧缓存
        _colorHelper = new CategoryColorHelper();
        // 重新从磁盘加载最新的分类颜色映射表
        _categoryColors = _colorHelper.Load();
        // 把新的颜色助手注入渲染器，后续绘制使用新配色
        _chartRenderer.SetColorHelper(_colorHelper);
        // 用户可能在设置页增删了分类，重建筛选下拉框选项
        LoadCategoryFilter();
        // 按当前周期重新聚合并重绘所有图表
        LoadData();
    }

    // ========== 周期切换 ==========

    /// <summary>
    /// 日/周/月单选按钮切换事件
    /// </summary>
    private void RbPeriod_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // XAML 初始化触发的事件忽略
        var tag = (string)((RadioButton)sender).Tag; // Tag 即周期标识
        if (tag == _period) return;                  // 与当前模式相同则不处理
        _period = tag;                // 切换模式
        _periodStart = DateTime.Today; // 参考日期重置为今天（新周期从当前开始浏览）
        // 按新模式刷新日期范围文字并加载对应 AI 总结
        UpdateRange();
        // 加载新模式的统计数据与图表
        LoadData();
    }

    /// <summary>
    /// 根据当前周期模式和参考日期，计算实际的日期范围（起止日期）
    /// </summary>
    /// <returns>(起始日期, 结束日期)</returns>
    private (DateTime start, DateTime end) GetRange()
    {
        switch (_period)
        {
            case "week": // 周一~周日：由 DayOfWeek 反推本周一与下周一-1
                // DayOfWeek 枚举中周日=0、周一=1……周六=6，先归一成「周一=1~周日=7」
                int delta = (int)_periodStart.DayOfWeek;
                if (delta == 0) delta = 7; // 周日归到本周末
                // 本周一 = 参考日前移 (delta-1) 天；本周日 = 参考日后移 (7-delta) 天
                return (_periodStart.AddDays(-(delta - 1)), _periodStart.AddDays(7 - delta));
            case "month": // 本月 1 日 ~ 月末
                // 先构造参考日期所在月的 1 号
                var first = new DateTime(_periodStart.Year, _periodStart.Month, 1);
                // 下月 1 号的前一天即本月最后一天（自动适配大小月与闰年）
                return (first, first.AddMonths(1).AddDays(-1));
            default: // 日模式：起止相同
                // 起止都取参考日期本身
                return (_periodStart, _periodStart);
        }
    }

    /// <summary>
    /// 更新日期范围显示文字、AI 总结标题、趋势图可见性，并加载 AI 总结
    /// </summary>
    private void UpdateRange()
    {
        var (s, e) = GetRange(); // 取当前周期起止
        // 根据周期模式设置显示文字和标题
        if (_period == "day")
        {
            RangeText.Text = s.ToString("MM-dd") + (s == DateTime.Today ? "（今天）" : ""); // 今天加后缀
            // 标题同步切换为「每日」
            AITitle.Text = "AI 每日总结";
            // 日模式不需要趋势图
            TrendSection.Visibility = Visibility.Collapsed;
        }
        else if (_period == "week")
        {
            RangeText.Text = $"{s:MM-dd} ~ {e:MM-dd}"; // 周显示区间
            // 标题同步切换为「每周」
            AITitle.Text = "AI 每周总结";
            TrendSection.Visibility = Visibility.Visible; // 周/月显示每日趋势
        }
        else
        {
            RangeText.Text = s.ToString("yyyy-MM"); // 月显示年-月
            // 标题同步切换为「每月」
            AITitle.Text = "AI 每月总结";
            TrendSection.Visibility = Visibility.Visible;
        }

        // 加载对应周期的 AI 总结
        LoadAISummary();
    }

    /// <summary>
    /// 判断当前周期是否是本周/本月
    /// </summary>
    private bool IsCurrentPeriod()
    {
        // 先算出当前查看周期的边界（对周/月比较才有意义）
        var (start, end) = GetRange();
        if (_period == "day") // 日模式：就是今天
            return _periodStart == DateTime.Today; // 参考日恰为今天，即处于“当前”
        if (_period == "week") // 周模式：起始等于本周一
        {
            var todayWeekStart = DateHelper.GetWeekStart(DateTime.Today); // 本周一（须与 GetRange 里的周一算法保持一致）
            return start == todayWeekStart; // 本周期起点是本周一 ⇒ 正在查看本周
        }
        // month: 年月均与当前一致
        return _periodStart.Year == DateTime.Today.Year && _periodStart.Month == DateTime.Today.Month; // 同年同月才算“当前月”
    }



    /// <summary>
    /// 从数据库加载 AI 总结并显示
    /// </summary>
    private void LoadAISummary()
    {
        // 切换周期时重置按钮文字（避免"正在生成..."串到其他周期）
        bool currentGen = _generatingByPeriod.TryGetValue(_period, out bool g) && g; // 只读“切换后新周期”自己的生成标志
        if (currentGen && _generatingPeriod == _period) // 新周期确实正处于生成中，这个提示才成立
            BtnGenerateAI.Content = "正在生成...";
        else // 该周期空闲（或上次生成已结束）→ 恢复成可点击的常规文案
            BtnGenerateAI.Content = "生成总结";

        string summaryType = _period switch { "week" => "weekly", "month" => "monthly", _ => "daily" }; // 周期 → 库中类型标识

        // 用 GetRange 算出来的起始日期查 AI 总结，而不是直接用 _periodStart
        // 因为 _periodStart 在周模式下可能是周中某天，但 AI 总结是按周一（周起始日）存的
        var (rangeStart, _) = GetRange();

        // ---------------- 日总结分支：manual 优先，历史日期回退 auto ----------------
        // 2026-08-25 修复：原实现只查 manual，导致每天 0:00 自动生成的日报（auto）永远不显示，
        // 用户误以为"每日总结没自动生成"。现改为：当前日期查 manual（可手动生成），
        // 历史日期无 manual 时回退显示 auto（由 SummaryScheduler 每天自动生成并入库）。
        if (_period == "day")
        {
            // 先查手动总结（当前周期手动生成）
            var (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "manual");
            // 历史日期（非今天）没有手动总结时，回退显示自动总结
            if (text == null && !IsCurrentPeriod())
                (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "auto");
            // 库中已有总结则直接展示
            if (text != null)
            {
                // 正文 Markdown 渲染 + 生成时间标签，同时缓存到 _currentAISummary
                AISummaryText.Markdown = text;
                AISummaryTime.Text = FormatSummaryTime(createdAt);
                _currentAISummary = text;
            }
                // 没有记录时显示引导文案，等待用户手动生成
            else
            {
                AISummaryText.Markdown = "点击「生成总结」获取 AI 分析...";
                AISummaryTime.Text = "";
                _currentAISummary = null;
            }
            // 日总结始终开放手动生成入口
            BtnGenerateAI.Visibility = Visibility.Visible;
        }
        else
        {
            // 周/月总结
            // 当前周期（本周/本月）走 manual 分支并提供生成按钮
            if (IsCurrentPeriod())
            {
                // 本周/月：查 manual，显示生成按钮
                var (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "manual");
                    // 已有总结则直接展示
                if (text != null)
                {
                    // 正文 Markdown 渲染 + 生成时间标签，同时缓存到 _currentAISummary
                    AISummaryText.Markdown = text;
                    AISummaryTime.Text = FormatSummaryTime(createdAt);
                    _currentAISummary = text;
                }
                    // 尚无总结时同样显示引导文案
                else
                {
                    AISummaryText.Markdown = "点击「生成总结」获取 AI 分析...";
                    AISummaryTime.Text = "";
                    _currentAISummary = null;
                }
                BtnGenerateAI.Visibility = Visibility.Visible;
            }
            else
            {
                // 非本周/月：查 auto，隐藏生成按钮
                var (text, createdAt) = AISummaryRepository.GetWithMeta(rangeStart, summaryType, "auto");
                    // 历史周期的自动总结存在则展示
                if (text != null)
                {
                    AISummaryText.Markdown = text;
                    AISummaryTime.Text = FormatSummaryTime(createdAt);
                    _currentAISummary = text;
                }
                else
                {
                    // 没有 auto 总结记录，只显示提示，不写数据库
                    string hint = _period == "week" ? "上周的总结将在下次启动程序时自动生成。" : "上个月的总结将在下次启动程序时自动生成。";
                    AISummaryText.Markdown = hint;
                    AISummaryTime.Text = "";
                    _currentAISummary = null;
                }
                BtnGenerateAI.Visibility = Visibility.Hidden;
            }
        }
    }

    /// <summary>
    /// 格式化总结时间显示，如 "8/3 20:30 总结"
    /// </summary>
    private static string FormatSummaryTime(string? createdAt)
    {
        if (string.IsNullOrEmpty(createdAt)) return ""; // 部分旧记录没存 CreatedAt，直接当空处理
        try
        {
            // CreatedAt 格式：yyyy-MM-dd HH:mm:ss.fff
            // 用 InvariantCulture 解析，失败会抛异常进入下方 catch 记日志
            var dt = DateTime.Parse(createdAt, CultureInfo.InvariantCulture);
            // 输出形如「8/3 20:30 总结」的简短时间戳
            return $"{dt:M/d} {dt:HH:mm} 总结";
        }
        catch (Exception ex) { Logger.Error("格式化总结时间失败", ex); return ""; } // 异常兜底：不中断展示流程
    }

    /// <summary>
    /// 「◀」按钮点击：回退到上一个日/周/月周期
    /// </summary>
    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        // 按周期回退一个单位（周-7天 / 月-1月 / 日-1天）
        switch (_period)
        {
            // 周模式：整体前移 7 天
            case "week": _periodStart = _periodStart.AddDays(-7); break;
            // 月模式：前移一个自然月
            case "month": _periodStart = _periodStart.AddMonths(-1); break;
            // 日模式：前移一天
            default: _periodStart = _periodStart.AddDays(-1); break;
        }
        // 刷新范围显示文字并加载该周期的 AI 总结
        UpdateRange();
        // 按新范围重新聚合并绘制图表
        LoadData();
    }

    /// <summary>
    /// 「▶」按钮点击：前进到下一个日/周/月周期（禁止越过当前期）
    /// </summary>
    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        // 取当前周期的结束日期用于「是否已到最新」的判断
        var (s, end) = GetRange();
        // 禁止翻到包含未来的周期（今天/本周/本月之后不可再前进）
        // 日模式：参考日已是今天就无法再前进
        if (_period == "day" && _periodStart >= DateTime.Today) return;
        // 周模式：结束日不早于今日说明已处于最新一周
        if (_period == "week" && end >= DateTime.Today) return;
        // 月模式：同理以月末判断是否已到本月
        if (_period == "month" && end >= DateTime.Today) return;

        // 按周期前进一个单位
        switch (_period)
        {
            // 周模式：整体后移 7 天
            case "week": _periodStart = _periodStart.AddDays(7); break;
            // 月模式：后移一个自然月
            case "month": _periodStart = _periodStart.AddMonths(1); break;
            // 日模式：后移一天
            default: _periodStart = _periodStart.AddDays(1); break;
        }
        // 刷新范围显示文字并加载 AI 总结
        UpdateRange();
        // 按新范围重新聚合并绘制图表
        LoadData();
    }

    /// <summary>
    /// 「本期」按钮点击：一键跳回今天/本周/本月
    /// </summary>
    private void BtnThis_Click(object sender, RoutedEventArgs e)
    {
        _periodStart = DateTime.Today; // 跳回当前周期
        // 重算日期范围并加载 AI 总结
        UpdateRange();
        // 按新范围重新聚合并绘制图表
        LoadData();
    }

    // ========== 分类筛选 ==========

    /// <summary>
    /// 加载分类筛选下拉框，第一项是"全部分类"
    /// </summary>
    private void LoadCategoryFilter()
    {
        CategoryFilter.Items.Clear(); // 清空旧选项
        var allItem = new ComboBoxItem { Content = "全部分类", Tag = "", IsSelected = true }; // 第一项=不过滤（约定 Tag 为空串）
        CategoryFilter.Items.Add(allItem); // “全部分类”本身也占一个下拉项，用户可随时切回
        try
        {
            var cats = CategoryRepository.GetAll(); // 读全部分类（Categories 表），供下拉逐项添加
            foreach (var cat in cats) // 遍历分类名
            {
                CategoryFilter.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Name }); // Content=显示名，Tag=分类名（供筛选比对）
            }
        }
        // 分类列表读取失败只记日志，保证页面其余功能可用
        catch (Exception ex) { Logger.Error("加载分类筛选列表失败", ex); }
        CategoryFilter.SelectedIndex = 0; // 默认选中"全部分类"
    }

    // ========== 数据加载 ==========

    /// <summary>
    /// 画布尺寸变化时用缓存数据重绘趋势图
    /// </summary>
    private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 仅当缓存过趋势数据时才重绘（首次布局时避免画空图）
        if (_cachedDailyData.Count > 0 || _cachedRangeStart != default) // 区间已缓存即重绘（数据为空也能画空图而非白屏）
            _chartRenderer.DrawTrendChart(TrendCanvas, _cachedDailyData, _cachedRangeStart, _cachedRangeEnd); // 按 LoadData 缓存的原范围重画
    }

    /// <summary>
    /// 从数据库加载当前周期的统计数据，刷新所有图表
    /// </summary>
    private void LoadData()
    {
        // 取当前周期的起止日期（日=当天、周=周一~周日、月=1号~月末）
        var (start, end) = GetRange();

        // 是否包含空闲时段、是否筛选了某个分类
        bool includeIdle = ChkSkipIdle.IsChecked != true; // “跳过空闲”勾选时置 false，查询端排除 IsIdle=1 的记录
        string filterCategory = GetSelectedFilterCategory(); // 下拉选中的分类（空串=不过滤，统计全部分类）

        // 三份聚合结果：分类占比 / 进程(Top应用)排行 / 按日总量
        Dictionary<string, int> catData;   // “分类名 → 秒数”，喂给占比条并求和成总时长
        Dictionary<string, int> procData;  // “进程名 → 秒数”，喂给 Top 应用榜
        Dictionary<string, int> dailyData; // “日期(yyyy-MM-dd) → 秒数”，喂给趋势折线图

        // ---------- 日模式：直查原始活动明细表 ----------
        if (_period == "day")
        {
            // 日模式直接在明细表 Activities 上做 SQL 聚合（单日范围扫描量小，重载时永远拿到最新数据）
            catData = ActivityRepository.GetCategorySummaryByRange(start, end, includeIdle);  // 分类聚合（includeIdle=false 时 SQL 追加 IsIdle=0）
            procData = ActivityRepository.GetProcessSummaryByRange(start, end, includeIdle);  // 进程聚合（Top 应用源数据）
            dailyData = ActivityRepository.GetDailyTotalsByRange(start, end, includeIdle);    // 逐日总量（日模式范围仅一天，故只产出今天这一行）

            // 筛选了特定分类时，只保留该分类的数据
            if (!string.IsNullOrEmpty(filterCategory))
            {
                catData = catData.Where(k => k.Key == filterCategory)
                    .ToDictionary(k => k.Key, v => v.Value);          // 类别占比只留选中项（下方总时长也随之只算选中分类）
                procData = FilterProcessByCategory(start, end, filterCategory); // 进程排行必须回明细重算：DailyProcessSummary 每天每进程只存主分类，按分类筛会漏掉次要分类的时长
            }
        }
        // ---------- 周/月模式：改读每日汇总表（扫描行数少、聚合更快） ----------
        else
        {
            // 周/月模式读每日预汇总表：把 N 天的二次聚合折到“天×分类/进程”的行数上，避开明细表全表扫描
            catData = DailySummaryRepository.GetCategorySummary(start, end); // 分类汇总（该表生成时已剔除空闲，故本接口没有 includeIdle 参数）
            dailyData = DailySummaryRepository.GetDailyTotals(start, end, includeIdle); // 趋势数据：includeIdle 决定取 TotalSeconds 还是 TotalActiveSeconds 列

            // Top 应用：有筛选则按类别查，否则查全部
            procData = DailySummaryRepository.GetProcessSummary(start, end,
                string.IsNullOrEmpty(filterCategory) ? null : filterCategory); // 分类条件直接下推到 SQL 过滤（进程表同样只存活跃秒数）

            // 类别筛选时只保留选中的类别
            if (!string.IsNullOrEmpty(filterCategory))
            {
                catData = catData.Where(k => k.Key == filterCategory) // 占比条与总时长只认选中分类
                    .ToDictionary(k => k.Key, v => v.Value);          // 重建字典，丢弃选中分类之外的条目
            }
        }

        // 计算总时长并显示
        int totalSeconds = catData.Values.Sum();      // 各分类求和=展示用总秒数（未勾“跳过空闲”时会把空闲一并算入）
        // 把总秒数转成 TimeSpan 便于拆解小时与分钟
        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);

        TotalText.Text = $"总活跃时长：{ts.Hours + ts.Days * 24}h{ts.Minutes}m"; // 跨天折算：TimeSpan 的天数部分摊进小时

        // 补充信息：日均时长
        if (_period == "day") // 日模式无需日均
            DetailText.Text = ""; // 副文案留空
        else if (_period == "week") // 周均摊 7 天
            DetailText.Text = $"日均：{totalSeconds / 7 / 3600}h{totalSeconds / 7 % 3600 / 60}m"; // 周总秒数÷7 得日均再拆 h/m（整除会丢弃余秒）
        else // 月模式：按当月实际天数均摊
        {
            int days = DateTime.DaysInMonth(start.Year, start.Month); // 月按实际天数均摊（大小月/闰年天数不同）
            DetailText.Text = $"日均：{totalSeconds / days / 3600}h{totalSeconds / days % 3600 / 60}m"; // 天均秒数 → h/m（整除丢弃余秒）
        }

        // 日模式筛选了某分类时隐藏类别占比栏（因为只有一个分类没意义）
        if (_period == "day" && !string.IsNullOrEmpty(filterCategory))
            CategorySection.Visibility = Visibility.Collapsed; // 收掉占比栏：单分类时展示它只会是恒定的 100%
        else
            CategorySection.Visibility = Visibility.Visible;   // 周/月模式或未筛分类时占比栏照常展示

        // 画各类图表
        _chartRenderer.DrawCategoryBars(CategoryBarsPanel, catData, totalSeconds); // 类别占比条

        // 缓存趋势数据，SizeChanged 时重绘
        _cachedDailyData = dailyData; // 逐日秒数（供重绘复用）
        _cachedRangeStart = start;    // 范围起点
        _cachedRangeEnd = end;        // 范围终点
        _chartRenderer.DrawTrendChart(TrendCanvas, dailyData, start, end); // 按当前日期范围画逐日趋势折线

        _chartRenderer.DrawTopApps(TopAppsPanel, procData); // Top 应用排行
    }

    /// <summary>读取筛选下拉当前选中的分类名（"" 表示不过滤）。</summary>
    private string GetSelectedFilterCategory()
    {
        if (CategoryFilter?.SelectedItem is ComboBoxItem item) // 空条件运算符兼作类型判断（防止控件未初始化）
            return item.Tag?.ToString() ?? ""; // Tag 里存的就是分类名；空 Tag 也按“不过滤”处理
        return ""; // 未选中任何项或控件不可用时的兜底：一律视为不过滤
    }

    /// <summary>分类筛选变化：重新加载数据与图表。</summary>
    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBarsPanel == null) return; // 控件未就绪（初始装载触发）忽略
        // 按新筛选条件重载全部统计与图表
        LoadData();
    }

    /// <summary>"跳过空闲"复选框变化：重新加载数据。</summary>
    private void ChkSkipIdle_Changed(object sender, RoutedEventArgs e)
    {
        if (CategoryBarsPanel == null) return;
        // 按新的空闲过滤开关重载统计与图表
        LoadData();
    }

    /// <summary>
    /// 日模式下按分类筛选进程：查原始活动表，过滤出指定分类的进程
    /// </summary>
    private Dictionary<string, int> FilterProcessByCategory(DateTime start, DateTime end, string category)
    {
        // 日模式 start==end；GetByRange 是左闭右开 [start,end)，要覆盖一整天必须把上界推后一天
        DateTime queryEnd = start.Date == end.Date ? end.AddDays(1) : end; // 只有“同一天”才 +1，多日区间直接沿用 end
        // 拉取区间内的原始活动明细记录
        var activities = ActivityRepository.GetByRange(start, queryEnd);
        // LINQ 管道：过滤指定分类 → 按进程分组 → 组内时长求和 → 时长降序 → 转字典
        // 末尾 ToDictionary 在 .NET 实际按插入序迭代，DrawTopApps 直接 Take(15)，此处的降序即最终榜单顺序
        return activities
            .Where(a => a.Category == category)      // 只留属于选中分类的活动
            .GroupBy(a => a.ProcessName)             // 按进程名分组
            .OrderByDescending(g => g.Sum(a => a.Duration)) // 组总时长降序，榜首排最前
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Duration)); // 进程名 → 区间总秒数
    }

    /// <summary>
    /// MarkdownScrollViewer 滚轮事件转交给外层 ScrollViewer
    /// </summary>
    private void AISummaryText_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // 标记已处理，不让 MarkdownScrollViewer 自己处理
        e.Handled = true;
        // 手动滚动外层 ScrollViewer
        MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta);
    }

    // ========== AI 总结 ==========

    // 当前显示的 AI 总结内容
    private string? _currentAISummary = null;
    // 按总结类型独立的生成状态：day/week/month → 是否正在生成
    private readonly Dictionary<string, bool> _generatingByPeriod = new() { ["day"] = false, ["week"] = false, ["month"] = false };
    // 记录正在生成的是哪个周期，防止 await 期间用户切换导致串台
    private string? _generatingPeriod = null;

    /// <summary>
    /// "生成总结"按钮点击事件：调用 AI 服务生成当前周期的总结，存库并自动保存文件
    /// </summary>
    private async void BtnGenerateAI_Click(object sender, RoutedEventArgs e)
    {
        string lockPeriod = _period; // 记录点击时的周期

        // 按总结类型独立防重复：日/周/月各自可并行，但单个类型正在生成时不能重复点
        if (_generatingByPeriod.TryGetValue(lockPeriod, out bool isGen) && isGen)
        {
            AISummaryText.Markdown = $"当前{(lockPeriod == "day" ? "日" : lockPeriod == "week" ? "周" : "月")}总结正在生成中，请等待完成。";
            return;
        }
        _generatingByPeriod[lockPeriod] = true; // 置该类型的"生成中"标志
        BtnGenerateAI.Content = "正在生成...";
        AISummaryText.Markdown = "正在生成...";

        // 锁定当前周期，防止 await 期间用户切换页面导致结果串台
        var (lockRangeStart, _) = GetRange(); // 只取范围起点：周=本周一、月=本月1号（AI 总结按该日期键入库/查询）
        DateTime lockPeriodStart = lockRangeStart; // 把起点固化为本地快照，供 await 结束后与“当时的周期”比对
        _generatingPeriod = lockPeriod;            // 全局标记“正在生成 lockPeriod”，配合防重复与防串台

        try
        {
            var aiService = new AISummaryService(); // 每次新建（读取最新 API 配置）

            // 根据锁定的周期调对应方法
            string? result; // 承接生成结果：成功返回 Markdown 正文，配置缺失/网络失败时可能为 null
            if (lockPeriod == "day") // 日：当天总结
                result = await aiService.GenerateDailySummary(lockPeriodStart);
            else if (lockPeriod == "week") // 周：整周总结（按周一 0 点取数）
                result = await aiService.GenerateWeeklySummary(lockPeriodStart);
            else // 月：整月总结（按本月 1 号 0 点取数）
                result = await aiService.GenerateMonthlySummary(lockPeriodStart);

            if (result != null) // 生成成功
            {
                _currentAISummary = result; // 正文先缓存到内存（刷新展示与后续落盘都基于它）

                // 存入数据库
                string summaryType = lockPeriod switch { "week" => "weekly", "month" => "monthly", _ => "daily" }; // 周期标识 → 库内类型字面量
                AISummaryRepository.Insert(lockPeriodStart, result, summaryType, "manual"); // 手动来源入库；Insert 按(日期,类型,来源)先删后插，只留最新一条

                // 自动保存到文件（按日期分文件夹，每次保留不覆盖）
                string? savePath = null;
                try
                {
                    savePath = AISummaryService.SaveSummaryToFile(result, lockPeriodStart, summaryType);
                }
                catch (Exception ex)
                {
                    Logger.Error("AI 总结自动保存失败", ex); // 文件保存失败不影响主流程
                }

                // 只有用户没切换走才刷新显示（用 GetRange 比较确保一致性）
                var (currentStart, _) = GetRange();
                if (lockPeriod == _period && lockPeriodStart == currentStart)
                {
                    LoadAISummary();
                }
            }
            else // AI 返回 null（配置缺失/网络失败等）
            {
                AISummaryText.Markdown = "生成失败，请检查设置页中的 AI API 配置。";
                Logger.Error("AI 总结生成返回 null");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AI 总结生成异常", ex);
            try { AISummaryText.Markdown = "生成失败，请查看日志。"; }
            catch { /* UI 不可用时忽略 */ }
        }
        finally
        {
            _generatingByPeriod[lockPeriod] = false; // 复位该类型生成标志
            _generatingPeriod = null;                // 清空“正在生成”的全局标记，解除防串台状态
            // 只有用户还在当前周期才重置按钮文字（否则由 LoadAISummary 管理）
            if (lockPeriod == _period)
                BtnGenerateAI.Content = "生成总结";
        }
    }
}
