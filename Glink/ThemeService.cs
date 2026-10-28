using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Glink.Core;

namespace Glink;

internal sealed record MoodTheme(string Id, string Name, string Description, bool IsLight,
    string Background, string Sidebar, string Card, string Border, string Hover,
    string Text, string Secondary, string Accent, string OnAccent, string Selection, string Waveform);

internal static class ThemeService
{
    public static IReadOnlyList<MoodTheme> Themes { get; } = new[]
    {
        new MoodTheme("twilight", "暮光", "暖炭灰与柔杏色，安静收纳每一份灵感。", false,
            "#171513", "#1F1C19", "#28231F", "#433B33", "#9D8067", "#F4EFE8", "#BEB0A2", "#E9AC83", "#292019", "#382C24", "#A18B78"),
        new MoodTheme("paper", "晨纸", "温润纸白与陶土红，轻盈而清晰。", true,
            "#F7F5F0", "#F0ECE3", "#FFFDF9", "#D8D0C2", "#9F826B", "#342D27", "#6C5F52", "#963D28", "#FFFFFF", "#EEE1D4", "#9B8977"),
        new MoodTheme("midnight", "夜航", "深海蓝与冰川青，留给专注的夜晚。", false,
            "#121923", "#18222F", "#202D3C", "#384A5E", "#6B91AA", "#EDF3F8", "#ACBED0", "#8DCBDC", "#132A34", "#273D4C", "#7694A9"),
        new MoodTheme("forest", "松林", "苔绿与浅金，沉静、自然、有呼吸感。", false,
            "#171D19", "#1E2821", "#28342B", "#435346", "#899C7D", "#EFF2E8", "#B7C4AF", "#C9D59A", "#263021", "#34422E", "#92A280"),
        new MoodTheme("ink", "墨", "纯黑与纸白，去掉一切色彩，只剩内容本身。", false,
            "#0A0A0A", "#131313", "#1B1B1B", "#383838", "#8A8A8A", "#F2F2F2", "#B0B0B0", "#F0F0F0", "#141414", "#282828", "#858585"),
        new MoodTheme("blank", "留白", "白纸与墨线，最干净的双色，让灵感自己说话。", true,
            "#FAFAFA", "#F1F1F1", "#FFFFFF", "#D6D6D6", "#8C8C8C", "#202020", "#606060", "#303030", "#FFFFFF", "#E8E8E8", "#949494"),
        new MoodTheme("celadon", "天青", "雨过天青，青瓷般的浅色，清爽而安静。", true,
            "#F4F8F7", "#E8F0ED", "#FCFEFD", "#CCDBD5", "#70968A", "#253B36", "#516C63", "#246B61", "#FFFFFF", "#DBEBE4", "#789D90"),
        new MoodTheme("blossom", "蔷薇", "淡粉与玫瑰灰，轻柔地包裹每一次记录。", true,
            "#FBF7F7", "#F3EAEC", "#FFFCFC", "#E2D1D5", "#B1848E", "#3D2B32", "#765963", "#9A4056", "#FFFFFF", "#F0DEE4", "#B58A97"),
        new MoodTheme("obsidian", "曜石", "烟紫与银灰，柔和照亮夜间的思绪。", false,
            "#19171F", "#211E29", "#2B2634", "#473E55", "#9380AD", "#F0ECF7", "#BEB1D0", "#C5ACE8", "#2B203C", "#3A2E4B", "#9A83B8"),
        new MoodTheme("coffee", "暖咖", "深咖与燕麦金，温暖而不刺眼。", false,
            "#211A17", "#2A211C", "#352A23", "#514035", "#A58669", "#F5EDE4", "#C9B4A1", "#DEB587", "#34261B", "#443226", "#A98B6D"),
        new MoodTheme("mist", "雾蓝", "雾白与石板蓝，清晰舒展的日间工作台。", true,
            "#F5F8FB", "#EBF0F6", "#FDFEFF", "#CFD9E4", "#7896B2", "#24364B", "#52677D", "#345F8A", "#FFFFFF", "#DFEAF4", "#819DB9"),
        new MoodTheme("sand", "沙金", "浅沙与古铜金，给文字留一份暖意。", true,
            "#FAF8F1", "#F2EEDF", "#FFFEF9", "#DED6BE", "#A59464", "#3E3725", "#706241", "#7C601F", "#FFFFFF", "#F0E7CD", "#A69359")
    };

    public static MoodTheme Current { get; private set; } = Themes[0];
    public static event Action? Changed;
    private static string SettingsPath => Path.Combine(Store.DataDirectory, "appearance.txt");

    public static void Load()
    {
        try
        {
            var id = File.ReadAllText(SettingsPath).Trim();
            Current = Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Apply(Current);
    }

    // Mutate shared brushes so existing cards, flyouts, and styles update without a reload.
    public static void Apply(MoodTheme theme)
    {
        var colors = new Dictionary<string, string>
        {
            ["BgBrush"] = theme.Background, ["SidebarBrush"] = theme.Sidebar,
            ["CardBrush"] = theme.Card, ["BorderBrush"] = theme.Border,
            ["BorderHoverBrush"] = theme.Hover, ["TextBrush"] = theme.Text,
            ["SubTextBrush"] = theme.Secondary, ["AccentBrush"] = theme.Accent,
            ["OnAccentBrush"] = theme.OnAccent, ["SelectionBrush"] = theme.Selection,
            ["WaveformBrush"] = theme.Waveform
        };
        foreach (var (key, hex) in colors)
            ((SolidColorBrush)Application.Current.Resources[key]).Color = CategoryVM.HexToColor(hex);
        // 选中底色直接使用调色板，避免二次混色降低选中文字与强调色的对比度。
        // 悬停与画布只保留轻微色温变化，让内容保持视觉主体。
        var background = CategoryVM.HexToColor(theme.Background);
        var card = CategoryVM.HexToColor(theme.Card);
        var accent = CategoryVM.HexToColor(theme.Accent);
        ((SolidColorBrush)Application.Current.Resources["CardHoverBrush"]).Color = Mix(card, accent, theme.IsLight ? 0.03 : 0.05);
        var glow = (LinearGradientBrush)Application.Current.Resources["CanvasGlowBrush"];
        glow.GradientStops[0].Color = Mix(background, accent, theme.IsLight ? 0.015 : 0.04);
        glow.GradientStops[1].Color = background;
        // 播放器控制面板遵循影视惯例：近黑底混入少量主题卡色、跨主题稳定，
        // 不随主题翻转成浅色（浅色药丸压黑底视频对比过强）。
        // 主题感由强调色与边框传达；浅色主题的强调色提亮 45% 保证在深色面板上可见。
        var border = CategoryVM.HexToColor(theme.Border);
        ((SolidColorBrush)Application.Current.Resources["PlayerPanelBrush"]).Color =
            Windows.UI.Color.FromArgb(204, (byte)(card.R * .2 + 16), (byte)(card.G * .2 + 16), (byte)(card.B * .2 + 16));
        ((SolidColorBrush)Application.Current.Resources["PlayerPanelBorderBrush"]).Color =
            Windows.UI.Color.FromArgb(89, border.R, border.G, border.B);
        static byte Lift(byte channel) => (byte)(channel + (255 - channel) * .45);
        ((SolidColorBrush)Application.Current.Resources["PlayerAccentBrush"]).Color = theme.IsLight
            ? Windows.UI.Color.FromArgb(255, Lift(accent.R), Lift(accent.G), Lift(accent.B))
            : accent;
        // Native control aliases are installed once in App.xaml. Re-inserting a
        // live brush under these keys throws 0x800F0902 during preset switching.
        Current = theme;
        Changed?.Invoke();
    }

    /// <summary>线性混色：t=0 得 a，t=1 得 b。派生色统一从这里出，保证各主题一致。</summary>
    internal static Windows.UI.Color Mix(Windows.UI.Color a, Windows.UI.Color b, double t) =>
        Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static void Save()
    {
        Directory.CreateDirectory(Store.DataDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, Current.Id);
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    internal static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        ApplyDialogTheme(dialog);
        return await dialog.ShowAsync();
    }

    internal static void ApplyDialogTheme(ContentDialog dialog)
    {
        // Popup 不继承 RootGrid 的主题；控件状态动画也会覆盖显式 Background。
        // 把模板实际读取的资源放在弹窗本地，优先于控件库的系统配色。
        var resources = Application.Current.Resources;
        dialog.RequestedTheme = Current.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        dialog.Background = (Brush)resources["CardBrush"];
        dialog.Foreground = (Brush)resources["TextBrush"];
        dialog.BorderBrush = (Brush)resources["BorderBrush"];
        dialog.CornerRadius = new CornerRadius(12);
        if (dialog.Content is TextBox input)
            input.PlaceholderForeground = (Brush)resources["SubTextBrush"];

        void Use(string brush, params string[] keys)
        {
            foreach (var key in keys) dialog.Resources[key] = resources[brush];
        }

        Use("CardBrush", "ContentDialogBackground", "ContentDialogTopOverlay", "TextControlBackgroundFocused");
        Use("SidebarBrush", "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundDisabled");
        Use("BorderBrush", "ContentDialogBorderBrush", "ContentDialogSeparatorBorderBrush",
            "ButtonBorderBrush", "ButtonBorderBrushDisabled", "ButtonBackgroundPressed",
            "TextControlBorderBrush", "TextControlBorderBrushDisabled");
        Use("BorderHoverBrush", "ButtonBorderBrushPointerOver", "TextControlBorderBrushPointerOver");
        Use("TransparentBrush", "ButtonBackground", "ButtonBackgroundDisabled", "TextControlButtonBackground");
        Use("SelectionBrush", "ButtonBackgroundPointerOver", "AccentButtonBackgroundDisabled", "AccentButtonBorderBrushDisabled",
            "TextControlButtonBackgroundPointerOver", "TextControlButtonBackgroundPressed");
        Use("TextBrush", "ContentDialogForeground", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
            "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "TextControlHeaderForeground",
            "TextControlButtonForeground", "TextControlButtonForegroundPointerOver", "TextControlButtonForegroundPressed");
        Use("SubTextBrush", "ButtonForegroundDisabled", "AccentButtonForegroundDisabled", "TextControlForegroundDisabled",
            "TextControlHeaderForegroundDisabled", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundDisabled");
        Use("AccentBrush", "AccentButtonBackground", "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed", "ButtonBorderBrushPressed", "TextControlBorderBrushFocused",
            "TextControlSelectionHighlightColor");
        Use("OnAccentBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");
        Use("TextBrush", "CheckBoxForeground", "CheckBoxForegroundPointerOver", "CheckBoxForegroundPressed",
            "CheckBoxForegroundChecked", "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed");
        Use("BorderBrush", "CheckBoxCheckBackgroundStrokeUnchecked");
        Use("BorderHoverBrush", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver");
        Use("TransparentBrush", "CheckBoxCheckBackgroundFillUnchecked");
        Use("SelectionBrush", "CheckBoxCheckBackgroundFillUncheckedPointerOver", "CheckBoxCheckBackgroundFillUncheckedPressed");
        Use("AccentBrush", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeChecked",
            "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed");
        Use("OnAccentBrush", "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed");

        var accent = CategoryVM.HexToColor(Current.Accent);
        dialog.Resources["FocusStrokeColorOuter"] = accent;
        dialog.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(Mix(accent, CategoryVM.HexToColor(Current.Text), .08));
        dialog.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(Mix(accent,
            CategoryVM.HexToColor(Current.IsLight ? Current.Text : Current.Background), .12));
        dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);
        dialog.Resources["OverlayCornerRadius"] = new CornerRadius(12);
        dialog.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1, 1, 1, 2);
    }

    // ====================================================================
    //  共享刷重赋：面板背景会跟随刷子原地改色，但代码或样式赋过刷子的
    //  TextBlock / FontIcon / 控件前景不会（渲染缓存），把同一个刷子对象
    //  重新赋一次即可触发重渲染。主题切换后对各窗口可视树整体重赋一遍。
    // ====================================================================

    private static readonly string[] SharedBrushKeys =
    {
        "BgBrush", "SidebarBrush", "CardBrush", "BorderBrush", "BorderHoverBrush",
        "TextBrush", "SubTextBrush", "AccentBrush", "OnAccentBrush", "SelectionBrush", "WaveformBrush",
        "CardHoverBrush", "CanvasGlowBrush",
        "PlayerAccentBrush", "PlayerPanelBrush", "PlayerPanelBorderBrush"
    };

    internal static void ReassertThemedBrushes(DependencyObject root)
    {
        if (root == null) return;
        var shared = new HashSet<Brush>(SharedBrushKeys.Select(key => (Brush)Application.Current.Resources[key]));
        Walk(root, shared);
    }

    private static void Walk(DependencyObject node, HashSet<Brush> shared)
    {
        // 各类型的 Foreground 是彼此独立的依赖属性，逐一处理；
        // 把当前值重新赋一次（同引用）即可触发重渲染。
        if (node is Control control)
        {
            if (control.Foreground is Brush fg && shared.Contains(fg)) control.Foreground = fg;
            if (control.Background is Brush bg && shared.Contains(bg)) control.Background = bg;
        }
        else if (node is TextBlock text && shared.Contains(text.Foreground)) text.Foreground = text.Foreground;
        else if (node is FontIcon icon && shared.Contains(icon.Foreground)) icon.Foreground = icon.Foreground;
        else if (node is RichTextBlock rich && shared.Contains(rich.Foreground)) rich.Foreground = rich.Foreground;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i), shared);
    }
}
