using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Media.Playback;
using Windows.System;

namespace Glink;

internal sealed partial class VideoViewerWindow : Window
{
    private readonly MediaPlayerElement _media;
    private readonly MediaPlayer _player;
    private readonly Grid _root;
    private readonly TextBlock _error;
    private readonly FontIcon _fullscreenIcon = new() { Glyph = "\uE740", FontSize = 15 };
    private RectInt32 _restoreBounds;
    private bool _fullscreenInstalled;

    internal VideoViewerWindow(MediaPlayer player, string fileName, double aspectRatio, AppWindow owner, double scale)
    {
        Title = fileName + " · Glink";
        _player = player;
        _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        _media = VideoCardView.CreateMediaElement();
        _media.SetMediaPlayer(player);
        _root.Children.Add(_media);
        _error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 440, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _root.Children.Add(_error);
        Content = _root;
        // Telegram 播放器式标题栏：画面顶满窗口，标题栏透明浮在画面上（图片查看器同款做法）。
        ExtendsContentIntoTitleBar = true;
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Viewer_KeyDown), true);
        _root.Loaded += (_, _) => _media.Focus(FocusState.Programmatic);
        // The template-defined cast button is replaced in place by a fullscreen toggle.
        // Templates may materialize parts late, so keep looking until the swap lands.
        _media.TransportControls.Loaded += (_, _) => InstallFullscreenButton();
        _media.TransportControls.LayoutUpdated += TransportControls_LayoutUpdated;
        ApplyTheme();
        ThemeService.Changed += ApplyTheme;
        Closed += (_, _) =>
        {
            _media.SetMediaPlayer(null);
            ThemeService.Changed -= ApplyTheme;
        };
        var area = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest).WorkArea;
        var maxWidth = Math.Max(1, (int)(area.Width * .85));
        var maxHeight = Math.Max(1, (int)(area.Height * .85));
        var width = Math.Min(maxWidth, (int)(960 * scale));
        var height = (int)(width / aspectRatio + 40 * scale);
        if (height > maxHeight) { height = maxHeight; width = Math.Min(maxWidth, (int)((height - 40 * scale) * aspectRatio)); }
        width = Math.Clamp(width, Math.Min(maxWidth, (int)(360 * scale)), maxWidth);
        height = Math.Clamp(height, Math.Min(maxHeight, (int)(260 * scale)), maxHeight);
        var x = Math.Clamp(owner.Position.X + (owner.Size.Width - width) / 2, area.X, area.X + area.Width - width);
        var y = Math.Clamp(owner.Position.Y + (owner.Size.Height - height) / 2, area.Y, area.Y + area.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.SetIcon("Assets/AppIcon.ico");
    }

    private void TransportControls_LayoutUpdated(object? sender, object e)
    {
        if (_fullscreenInstalled) _media.TransportControls.LayoutUpdated -= TransportControls_LayoutUpdated;
        else InstallFullscreenButton();
    }

    private void InstallFullscreenButton()
    {
        if (_fullscreenInstalled) return;
        try
        {
            var cast = FindDescendantByName(_media.TransportControls, "CastButton");
            if (cast == null) return;
            _fullscreenInstalled = true;
            // Hiding alone is not enough: MediaTransportControls flips the cast
            // button back to Visible once casting becomes available. Swap the
            // command itself so a fullscreen toggle takes its exact slot.
            if (FindDescendantByName(_media.TransportControls, "MediaControlsCommandBar") is CommandBar bar &&
                cast is ICommandBarElement command)
            {
                var index = bar.PrimaryCommands.IndexOf(command);
                var button = new AppBarButton
                {
                    Style = cast.Style,
                    Icon = _fullscreenIcon,
                    Label = "全屏",
                    MinWidth = 0
                };
                ToolTipService.SetToolTip(button, "全屏 / 退出全屏 (F11)");
                AutomationProperties.SetName(button, "全屏切换");
                button.Click += (_, _) => ToggleFullScreen();
                if (index >= 0) bar.PrimaryCommands.RemoveAt(index);
                bar.PrimaryCommands.Insert(index >= 0 ? index : bar.PrimaryCommands.Count, button);
                return;
            }
            // Fallback for template drift: keep the stock commands and only hide cast.
            cast.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Keep the stock transport controls if the template differs from expectations.
        }
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject node, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var found = FindDescendantByName(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private void ToggleFullScreen()
    {
        if (IsFullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.MoveAndResize(_restoreBounds);
            _fullscreenIcon.Glyph = "\uE740";
        }
        else
        {
            _restoreBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _fullscreenIcon.Glyph = "\uE923";
        }
    }

    private void ApplyTheme()
    {
        // 播放窗口是"影院面"：根主题固定深色，控制面板遵循影视惯例跨主题稳定，
        // 主题感由 ThemeService 计算的强调色/边框传达（见 App.xaml 播放器桥接）。
        _root.RequestedTheme = ElementTheme.Dark;
        // 实时切换主题后重赋共享刷：播放控件的时间文字/按钮等模板文字才会跟上新主题。
        ThemeService.ReassertThemedBrushes(_root);
        // 画面永远衬在黑底上，标题按钮跨主题统一用白色系（浅色主题的深色文字在视频上不可见）；
        // 面板与滑杆颜色已由 App.xaml 桥接的主题刷接管，随主题联动。
        var title = AppWindow.TitleBar;
        title.BackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ForegroundColor = Windows.UI.Color.FromArgb(255, 0xF5, 0xF5, 0xF5);
        title.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0xF5, 0xF5, 0xF5);
        title.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        title.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(51, 0xFF, 0xFF, 0xFF);
        title.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        title.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(34, 0xFF, 0xFF, 0xFF);
        title.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(153, 0xFF, 0xFF, 0xFF);
    }

    internal void ShowError(string message)
    {
        _error.Text = message; _error.Visibility = Visibility.Visible;
        _media.AreTransportControlsEnabled = false;
    }

    private void Viewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            // Esc first leaves fullscreen; a second Esc closes the viewer.
            if (IsFullScreen) ToggleFullScreen();
            else Close();
            return;
        }
        if (e.Key == VirtualKey.F11)
        {
            e.Handled = true;
            ToggleFullScreen();
            return;
        }
        if (e.Handled || VideoCardView.IsPlaybackControl(e.OriginalSource)) return;
        if (e.Key == VirtualKey.Space)
        {
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play();
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            var seconds = _player.PlaybackSession.Position.TotalSeconds + (e.Key == VirtualKey.Right ? 5 : -5);
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _player.PlaybackSession.NaturalDuration.TotalSeconds));
            e.Handled = true;
        }
    }
}
