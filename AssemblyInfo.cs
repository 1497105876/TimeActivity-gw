// ============================================================================
// AssemblyInfo.cs — 程序集级特性声明
// ThemeInfo 指定 WPF 主题资源字典的查找位置：
//   泛型资源(generic.xaml)位于本程序集，主题特定资源不使用外部程序集。
// 由 WPF 项目模板生成，一般无需修改。
// ============================================================================
using System.Windows;

// ThemeInfo：声明主题/泛型资源字典位置（WPF 控件模板查找约定）
[assembly:ThemeInfo(
    // 参数1 None：主题特定资源字典不在外部主题程序集中查找
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    // 参数2 SourceAssembly：泛型资源字典(generic.xaml)位于本程序集内
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
