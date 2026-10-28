using System.IO;
using Microsoft.UI.Xaml;

namespace Glink;

public partial class App : Application
{
    private Window? _window;

    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        InstallCrashLog();
    }

    /// <summary>
    /// 播放器桥接必须同时进主题字典：视频查看器根元素固定 ElementTheme.Dark
    /// （影院面），XAML 资源解析会优先命中控件库主题字典的 Dark 默认值（系统
    /// 强调色蓝、默认灰面板），普通区的同名覆盖会被绕过。XAML 属性元素必须先于
    /// 全部内容声明、而 StaticResource 又不能前向引用，两难之下改为代码注入：
    /// 向 Default/Light/Dark 三个主题字典插入**同一个共享刷实例**——ThemeService
    /// 原地改色时所有变体自动联动。普通区（App.xaml）的同名桥接保留作兜底。
    /// </summary>
    private static void InstallPlayerThemeBridges()
    {
        (string Key, string Brush)[] bridges =
        {
            ("MediaTransportControlsPanelBackground", "PlayerPanelBrush"),
            ("MediaTransportControlsBorderBrush", "PlayerPanelBorderBrush"),
            ("MediaTransportControlsFillMediaText", "PlayerTextBrush"),
            ("MediaTransportControlsFillTimeElapsedText", "PlayerTextDimBrush"),
            ("MediaTransportControlsFlyoutBackground", "PlayerFlyoutBrush"),
            ("MediaTransportControlsThumbBorderBackground", "PlayerFlyoutBrush"),
            ("AppBarButtonForeground", "PlayerTextBrush"),
            ("AppBarButtonForegroundPointerOver", "PlayerAccentBrush"),
            ("AppBarButtonForegroundPressed", "PlayerAccentBrush"),
            ("AppBarButtonForegroundDisabled", "PlayerTextDimBrush"),
            ("AppBarButtonBackgroundPointerOver", "TransparentBrush"),
            ("AppBarButtonBackgroundPressed", "TransparentBrush"),
            ("AppBarToggleButtonForeground", "PlayerTextBrush"),
            ("AppBarToggleButtonForegroundPointerOver", "PlayerAccentBrush"),
            ("AppBarToggleButtonForegroundPressed", "PlayerAccentBrush"),
            ("AppBarToggleButtonForegroundChecked", "PlayerAccentBrush"),
            ("AppBarToggleButtonForegroundCheckedPointerOver", "PlayerAccentBrush"),
            ("AppBarToggleButtonForegroundCheckedPressed", "PlayerAccentBrush"),
            ("AppBarToggleButtonBackgroundPointerOver", "TransparentBrush"),
            ("AppBarToggleButtonBackgroundPressed", "TransparentBrush"),
            ("AppBarToggleButtonBackgroundChecked", "TransparentBrush"),
            ("AppBarToggleButtonBackgroundCheckedPointerOver", "TransparentBrush"),
            ("AppBarToggleButtonBackgroundCheckedPressed", "TransparentBrush"),
            ("CommandBarBackground", "TransparentBrush"),
            ("CommandBarForeground", "PlayerTextBrush"),
            ("SliderTrackValueFill", "PlayerAccentBrush"),
            ("SliderTrackValueFillPointerOver", "PlayerAccentBrush"),
            ("SliderTrackValueFillPressed", "PlayerAccentBrush"),
            ("SliderTrackFill", "PlayerTrackBrush"),
            ("SliderTrackFillPointerOver", "PlayerTrackBrush"),
            ("SliderTrackFillPressed", "PlayerTrackBrush"),
            ("SliderThumbBackground", "PlayerAccentBrush"),
            ("SliderThumbBackgroundPointerOver", "PlayerAccentBrush"),
            ("SliderThumbBackgroundPressed", "PlayerAccentBrush"),
            // ContentDialog 弹层同样按主题字典解析（同根因），普通区桥接与显式
            // Background 属性都会被 ThemeResource 的 Dark 变体绕过。
            ("ContentDialogBackground", "CardBrush"),
            ("ContentDialogBorderBrush", "BorderBrush"),
            ("ContentDialogForeground", "TextBrush"),
            ("ContentDialogPrimaryButtonBackground", "AccentBrush"),
            ("ContentDialogPrimaryButtonBackgroundPointerOver", "AccentBrush"),
            ("ContentDialogPrimaryButtonBackgroundPressed", "AccentBrush"),
            ("ContentDialogPrimaryButtonForeground", "OnAccentBrush"),
            ("ContentDialogPrimaryButtonForegroundPointerOver", "OnAccentBrush"),
            ("ContentDialogPrimaryButtonForegroundPressed", "OnAccentBrush"),
            ("ContentDialogSecondaryButtonBackground", "TransparentBrush"),
            ("ContentDialogSecondaryButtonBackgroundPointerOver", "SelectionBrush"),
            ("ContentDialogSecondaryButtonBackgroundPressed", "SelectionBrush"),
            ("ContentDialogSecondaryButtonForeground", "TextBrush"),
            ("ContentDialogSecondaryButtonForegroundPointerOver", "TextBrush"),
            ("ContentDialogSecondaryButtonForegroundPressed", "TextBrush"),
            ("ContentDialogCloseButtonBackground", "TransparentBrush"),
            ("ContentDialogCloseButtonBackgroundPointerOver", "SelectionBrush"),
            ("ContentDialogCloseButtonBackgroundPressed", "SelectionBrush"),
            ("ContentDialogCloseButtonForeground", "TextBrush"),
            ("ContentDialogCloseButtonForegroundPointerOver", "TextBrush"),
            ("ContentDialogCloseButtonForegroundPressed", "TextBrush"),
            // 对话框内的输入框（TextBox 主题键同样走主题字典）。
            ("TextControlBackground", "SidebarBrush"),
            ("TextControlBackgroundPointerOver", "SidebarBrush"),
            ("TextControlBackgroundFocused", "CardBrush"),
            ("TextControlBackgroundDisabled", "SidebarBrush"),
            ("TextControlBorderBrush", "BorderBrush"),
            ("TextControlBorderBrushPointerOver", "BorderHoverBrush"),
            ("TextControlBorderBrushFocused", "AccentBrush"),
            ("TextControlBorderBrushDisabled", "BorderBrush"),
            ("TextControlForeground", "TextBrush"),
            ("TextControlForegroundPointerOver", "TextBrush"),
            ("TextControlForegroundFocused", "TextBrush"),
            ("TextControlPlaceholderForeground", "SubTextBrush"),
            ("TextControlPlaceholderForegroundPointerOver", "SubTextBrush"),
            ("TextControlPlaceholderForegroundFocused", "SubTextBrush"),
            ("TextControlSelectionHighlightColor", "AccentBrush"),
        };
        try
        {
            var resources = Current.Resources;
            foreach (var themeKey in new[] { "Default", "Light", "Dark" })
            {
                // 应用级 ThemeDictionaries 原本为空（控件库的主题字典在合并字典里，
                // 查找层级更靠内）——必须在此创建同级字典，键才能抢先命中。
                if (!resources.ThemeDictionaries.TryGetValue(themeKey, out var theme) || theme is not ResourceDictionary dict)
                {
                    dict = new ResourceDictionary();
                    resources.ThemeDictionaries[themeKey] = dict;
                }
                foreach (var (key, brush) in bridges)
                    dict[key] = resources[brush];
            }
        }
        catch
        {
            // 主题字典注入失败时普通区桥接仍然生效（主窗口不受影响），
            // 只是弹层（ContentDialog/播放器）会看到部分默认色。
        }
    }

    /// <summary>
    /// 保留未处理异常，避免将正常框架探测产生的首见异常写入用户日志。
    /// 日志超过 5MB 自动重开。
    /// </summary>
    private void InstallCrashLog()
    {
        var path = Path.Combine(Glink.Core.Store.DataDirectory, "crash.log");
        string Stamp(Exception ex) =>
            $"[{DateTime.Now:MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n---\n";
        void Append(string text)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 5_000_000) info.Delete();
                File.AppendAllText(path, text);
            }
            catch { /* 日志绝不能反过来影响运行 */ }
        }

        UnhandledException += (_, e) => Append("UNHANDLED " + Stamp(e.Exception));
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // OnLaunched 时 XamlControlsResources 已完全加载（App 构造早期访问
        // Resources/ThemeDictionaries 会因状态未就绪而失败）。
        InstallPlayerThemeBridges();

        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();
#if CANVAS_REVIEW
        _ = ((MainWindow)_window).RunCanvasReviewAsync();
#endif
    }
}
