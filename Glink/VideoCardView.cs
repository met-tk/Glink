using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Glink.Core;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Glink;

/// <summary>
/// 视频卡片：画面即卡片主体，无原生控件浮层。
/// 交互 —— 单击播放/暂停 · 双击放大 · 底部时间轴拖动 seek · 右下角放大按钮。
/// 视觉全部走主题刷（AccentBrush/OnAccentBrush/WaveformBrush…），随四套预设联动；
/// 画面上的时长徽章与提示芯片用半透明黑色（影视惯例，跨主题稳定）。
/// </summary>
internal sealed partial class VideoCardView : Grid, IDisposable
{
    private readonly ItemVM _item;
    private readonly AppWindow _owner;
    private readonly Action<VideoCardView> _playbackRequested;
    private readonly Action<VideoCardView>? _activated;
    private readonly MediaPlayerElement _media;
    private readonly Grid _surface;
    private readonly Image _poster;
    private readonly Button _playButton;
    private readonly ProgressRing _loading;
    private readonly Border _statusChip;
    private readonly TextBlock _statusText;
    private readonly Border _durationBadge;
    private readonly TextBlock _durationText;
    private readonly Grid _seekRow;
    private readonly Border _seekTrack;
    private readonly Border _seekFill;
    private readonly Border _seekThumb;
    private readonly TextBlock _positionText;
    private readonly TextBlock _totalText;
    private readonly FontIcon _typeIcon;
    private readonly TextBlock _fileNameText;
    private readonly Border _footerSeparator;
    private readonly Button _expandButton;
    private readonly DispatcherQueueTimer _tapTimer;
    private MediaPlayer? _player;
    private long _lastSurfaceTapTick;
    private MediaSource? _source;
    private VideoViewerWindow? _viewer;
    private bool _disposed;
    private bool _playRequested;
    private bool _failed;
    private bool _seekDragging;
    private double? _pendingSeekRatio;
    internal Guid ItemId => _item.Id;
    internal bool IsViewerOpen => _viewer != null;

    // ── 画布边缘调整大小：画面表面是卡片的可缩放主体 ──────────────────
    internal double AspectRatio => _item.VideoAspectRatio;
    /// <summary>画面表面当前高度（构造时设定、只随调整大小变化，布局未跑也准确）。</summary>
    internal double SurfaceHeight => _surface.Height;
    /// <summary>按表面宽度换算表面高度（等比缩放，钳制口径与构造一致）。</summary>
    internal void ApplySurfaceWidth(double width) =>
        _surface.Height = Math.Clamp(width / _item.VideoAspectRatio, 140, 760);
    /// <summary>放大按钮在本视图坐标系里的外接矩形（把手命中让位给可见控件）。</summary>
    internal Rect ExpandButtonBounds
    {
        get
        {
            if (_expandButton.ActualWidth <= 0 || _expandButton.ActualHeight <= 0) return new Rect(0, 0, 0, 0);
            try
            {
                var origin = _expandButton.TransformToVisual(this).TransformPoint(new Point());
                return new Rect(origin, new Size(_expandButton.ActualWidth, _expandButton.ActualHeight));
            }
            catch { return new Rect(0, 0, 0, 0); }
        }
    }

    public VideoCardView(ItemVM item, AppWindow owner, Action<VideoCardView> playbackRequested, double surfaceWidth = 358, Action<VideoCardView>? activated = null)
    {
        _item = item;
        _owner = owner;
        _playbackRequested = playbackRequested;
        // 画布卡片本身不可聚焦，空格路由需要主窗口知道"最近交互的是哪张卡"。
        _activated = activated;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var surface = new Grid
        {
            Height = Math.Clamp(surfaceWidth / item.VideoAspectRatio, 140, 760),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            CornerRadius = new CornerRadius(11, 11, 0, 0)
        };
        _surface = surface;
        _poster = new Image { Source = item.ImageSource, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        _media = CreateMediaElement();
        _media.AreTransportControlsEnabled = false;
        _media.Visibility = Visibility.Collapsed;
        surface.Children.Add(_poster);
        surface.Children.Add(_media);

        _playButton = new Button
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28),
            Background = Brush("AccentBrush"),
            Foreground = Brush("OnAccentBrush"),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE768", FontSize = 20 },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_playButton, "播放视频");
        surface.Children.Add(_playButton);

        _loading = new ProgressRing
        {
            Width = 30, Height = 32, IsActive = false, Visibility = Visibility.Collapsed,
            IsHitTestVisible = false, Foreground = Brush("AccentBrush")
        };
        surface.Children.Add(_loading);

        _durationText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        _durationBadge = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(168, 18, 18, 18)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 10),
            IsHitTestVisible = false,
            Child = _durationText
        };
        surface.Children.Add(_durationBadge);

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 320
        };
        _statusChip = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(188, 16, 16, 16)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 12, 12),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _statusText
        };
        surface.Children.Add(_statusChip);
        Children.Add(surface);

        // ── 时间轴：当前时间 · 轨道(填充+手柄) · 总时长 ──────────────────
        _seekRow = new Grid { Height = 26, Margin = new Thickness(12, 5, 12, 0), ColumnSpacing = 10 };
        _seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _seekRow.ColumnDefinitions.Add(new ColumnDefinition());
        _seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _positionText = new TextBlock
        {
            Text = "0:00", FontSize = 11, Foreground = Brush("SubTextBrush"),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        Grid.SetColumn(_positionText, 0);
        _seekRow.Children.Add(_positionText);

        _seekTrack = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush("WaveformBrush")
        };
        _seekFill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left, Width = 0,
            Background = Brush("AccentBrush"), IsHitTestVisible = false
        };
        _seekTrack.Child = _seekFill;
        _seekThumb = new Border
        {
            Width = 11, Height = 11, CornerRadius = new CornerRadius(5.5),
            Background = Brush("AccentBrush"),
            BorderThickness = new Thickness(2), BorderBrush = Brush("CardBrush"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        var trackHost = new Grid();
        trackHost.Children.Add(_seekTrack);
        trackHost.Children.Add(_seekThumb);
        Grid.SetColumn(trackHost, 1);
        _seekRow.Children.Add(trackHost);

        _totalText = new TextBlock
        {
            Text = FormatDuration(item.VideoDuration), FontSize = 11,
            Foreground = Brush("SubTextBrush"),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        Grid.SetColumn(_totalText, 2);
        _seekRow.Children.Add(_totalText);

        ToolTipService.SetToolTip(_seekRow, "拖动调整进度");
        AutomationProperties.SetName(_seekRow, "视频进度条");
        _seekRow.PointerEntered += (_, _) => SetSeekHover(true);
        _seekRow.PointerExited += (_, _) => SetSeekHover(_seekDragging);
        _seekRow.PointerPressed += SeekRow_PointerPressed;
        _seekRow.PointerMoved += SeekRow_PointerMoved;
        _seekRow.PointerReleased += SeekRow_PointerReleased;
        _seekRow.PointerCaptureLost += SeekRow_PointerCaptureLost;
        _seekRow.PointerCanceled += SeekRow_PointerCaptureLost;
        _seekRow.SizeChanged += (_, _) => UpdateSeekBar();
        Children.Add(_seekRow);
        Grid.SetRow(_seekRow, 1);

        // ── 页脚：类型图标 · 文件名 · 放大 ─────────────────────────────
        var footerArea = new StackPanel();
        _footerSeparator = new Border
        {
            Height = 1, Background = Brush("BorderBrush"), Opacity = .55,
            Margin = new Thickness(12, 6, 12, 0)
        };
        footerArea.Children.Add(_footerSeparator);
        var footer = new Grid { Padding = new Thickness(12, 5, 4, 4), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _typeIcon = new FontIcon
        {
            Glyph = "\uE714", FontSize = 13, Foreground = Brush("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_typeIcon, 0);
        footer.Children.Add(_typeIcon);
        _fileNameText = new TextBlock
        {
            Text = item.FileName ?? "视频", FontSize = 12, Foreground = Brush("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(_fileNameText, item.FileName);
        Grid.SetColumn(_fileNameText, 1);
        footer.Children.Add(_fileNameText);
        _expandButton = new Button
        {
            Style = (Style)Application.Current.Resources["ToolButtonStyle"],
            Width = 32, Height = 32,
            Foreground = Brush("SubTextBrush"),
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 14 }
        };
        AutomationProperties.SetName(_expandButton, "放大视频");
        ToolTipService.SetToolTip(_expandButton, "放大视频 · 也可以双击画面");
        _expandButton.Click += (_, _) => OpenViewer();
        Grid.SetColumn(_expandButton, 2);
        footer.Children.Add(_expandButton);
        footerArea.Children.Add(footer);
        Grid.SetRow(footerArea, 2);
        Children.Add(footerArea);

        _tapTimer = DispatcherQueue.CreateTimer();
        _tapTimer.IsRepeating = false;
        _tapTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _tapTimer.Tick += (_, _) => TogglePlayback();
        _playButton.Click += (_, _) =>
        {
            if (_viewer != null) return;
            _tapTimer.Stop(); _tapTimer.Start();
        };
        _playButton.DoubleTapped += (_, e) => { _tapTimer.Stop(); OpenViewer(); e.Handled = true; };
        surface.Tapped += (_, e) =>
        {
            if (IsPlaybackControl(e.OriginalSource)) return;
            _tapTimer.Stop(); _tapTimer.Start(); e.Handled = true;
        };
        surface.DoubleTapped += (_, e) =>
        {
            if (IsPlaybackControl(e.OriginalSource)) return;
            _tapTimer.Stop(); OpenViewer(); e.Handled = true;
        };
        _media.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, e) =>
        {
            if (IsPlaybackControl(e.OriginalSource)) return;
            _tapTimer.Stop(); _tapTimer.Start(); e.Handled = true;
        }), true);
        _media.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, e) =>
        {
            if (IsPlaybackControl(e.OriginalSource)) return;
            _tapTimer.Stop(); OpenViewer(); e.Handled = true;
        }), true);
        // 点按/拖动阈值分流：画面内部按下不吞掉指针事件（冒泡给卡片 Border），
        // 由卡片的标准拖动阈值判定 —— 移动超过阈值 → 拖动卡片；原位松开 → 点按播放
        // （见 MainWindow.Drag.cs 释放路径）。列表模式无指针捕获，surface 的
        // Tapped 手势仍然可达，作为该模式点按播放的兜底路径。
        UpdateSeekBar();
        // Button/TextBlock renders do not follow in-place brush mutations the way
        // panel backgrounds do, so the card re-asserts its themed brushes on switch.
        ThemeService.Changed += ApplyCardTheme;
    }

    private void ApplyCardTheme()
    {
        _playButton.Background = Brush("AccentBrush");
        _playButton.Foreground = Brush("OnAccentBrush");
        _loading.Foreground = Brush("AccentBrush");
        _seekTrack.Background = Brush("WaveformBrush");
        _seekFill.Background = Brush("AccentBrush");
        _seekThumb.Background = Brush("AccentBrush");
        _seekThumb.BorderBrush = Brush("CardBrush");
        _positionText.Foreground = Brush("SubTextBrush");
        _totalText.Foreground = Brush("SubTextBrush");
        _typeIcon.Foreground = Brush("AccentBrush");
        _fileNameText.Foreground = Brush("TextBrush");
        _footerSeparator.Background = Brush("BorderBrush");
        _expandButton.Foreground = Brush("SubTextBrush");
    }

    private void SetSeekHover(bool active)
    {
        _seekTrack.Height = active ? 6 : 4;
        _seekFill.Height = active ? 6 : 4;
        _seekThumb.Visibility = active || _seekDragging ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static MediaPlayerElement CreateMediaElement() => new()
    {
        AutoPlay = false, AreTransportControlsEnabled = true, Stretch = Stretch.Uniform,
        RequestedTheme = ElementTheme.Dark,
        TransportControls = new MediaTransportControls
        {
            IsCompact = false, IsZoomButtonVisible = false, IsPlaybackRateButtonVisible = false,
            IsFastForwardButtonVisible = false, IsFastRewindButtonVisible = false,
            IsSkipForwardButtonVisible = false, IsSkipBackwardButtonVisible = false
        }
    };

    internal static bool IsPlaybackControl(object source)
    {
        for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or Slider or CommandBar) return true;
            // MediaTransportControls spans the whole video, including the picture.
            // Only its actual controls should suppress picture tap/double-tap gestures.
            if (node is MediaTransportControls) break;
            if (node is VideoCardView) break;
        }
        return false;
    }

    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private bool EnsurePlayer()
    {
        if (_disposed) return false;
        if (_player != null && !_failed) return true;
        if (_failed) _viewer?.Close();
        ReleasePlayer();
        try
        {
            if (string.IsNullOrEmpty(_item.FilePath) || !File.Exists(Store.AbsolutePath(_item.FilePath)))
                throw new FileNotFoundException();
            _failed = false;
            AutomationProperties.SetName(_playButton, "播放视频");
            _statusChip.Visibility = Visibility.Collapsed;
            _player = new MediaPlayer { AutoPlay = false };
            _player.MediaOpened += Player_MediaOpened;
            _player.MediaFailed += Player_MediaFailed;
            _player.MediaEnded += Player_MediaEnded;
            _player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
            _player.PlaybackSession.PositionChanged += Session_PositionChanged;
            _media.SetMediaPlayer(_player);
            _source = MediaSource.CreateFromUri(new Uri(Store.AbsolutePath(_item.FilePath)));
            _player.Source = _source;
            return true;
        }
        catch (Exception error)
        {
            ShowFailure(error is FileNotFoundException ? "找不到视频文件，请重新导入。" : "视频暂时无法打开，请重试。");
            return false;
        }
    }

    // 该卡片成为空格的目标：点击画面/播放、拖进度、放大播放都算激活。
    private void MarkActive() => _activated?.Invoke(this);

    /// <summary>
    /// 判定卡片局部坐标是否落在画面内部的"点按区"：避开外缘 12-DIP 让位环
    /// （该环归边缘调整大小把手），页脚/时间轴不在画面范围内自然排除。
    /// </summary>
    internal bool IsInsideSurfaceInner(Point cardLocal)
    {
        var origin = _surface.TransformToVisual(this).TransformPoint(new Point(0, 0));
        var x = cardLocal.X - origin.X;
        var y = cardLocal.Y - origin.Y;
        return x > 12 && x < _surface.ActualWidth - 12 && y > 12 && y < _surface.ActualHeight - 12;
    }

    /// <summary>
    /// 画面内部点按（按下-松开未超过拖动阈值）：单击延时播放/暂停（与双击区分），
    /// 双击时限内第二次点按直接放大播放。与卡片 DoubleTapped 互为兜底，均有去重保护。
    /// </summary>
    internal void HandleSurfaceTap()
    {
        if (_disposed || _viewer != null) return;
        var now = Environment.TickCount64;
        if (_lastSurfaceTapTick != 0 && now - _lastSurfaceTapTick <= GetDoubleClickTime())
        {
            _lastSurfaceTapTick = 0;
            _tapTimer.Stop();
            OpenViewer();
            return;
        }
        _lastSurfaceTapTick = now;
        _tapTimer.Stop();
        _tapTimer.Start();
    }

    internal void TogglePlayback()
    {
        if (_disposed) return;
        _tapTimer.Stop();
        MarkActive();
        if (_playRequested && !_failed) { Pause(); return; }
        StartPlayback();
    }

    private void StartPlayback()
    {
        if (!EnsurePlayer()) return;
        _playbackRequested(this);
        _playRequested = true;
        _playButton.Visibility = Visibility.Collapsed;
        _loading.Visibility = Visibility.Visible;
        _loading.IsActive = true;
        _player!.Play();
    }

    internal void Pause()
    {
        _tapTimer.Stop();
        _playRequested = false;
        _player?.Pause();
        _item.IsPlaying = false;
    }

    internal void OpenViewer()
    {
        if (_disposed) return;
        _tapTimer.Stop();
        MarkActive();
        if (_viewer != null) { _viewer.Activate(); return; }
        var startPlayback = _player == null || _failed;
        if (!EnsurePlayer()) return;
        if (startPlayback) StartPlayback();
        _media.SetMediaPlayer(null);
        _media.Visibility = Visibility.Collapsed;
        _poster.Visibility = Visibility.Visible;
        _playButton.Visibility = Visibility.Collapsed;
        ShowStatus("正在独立窗口中播放");
        try
        {
            _viewer = new VideoViewerWindow(_player!, _item.FileName ?? "视频", _item.VideoAspectRatio, _owner, XamlRoot?.RasterizationScale ?? 1);
            _viewer.Closed += (_, _) =>
            {
                _viewer = null;
                if (_disposed || _player == null) return;
                _media.SetMediaPlayer(_player);
                _statusChip.Visibility = Visibility.Collapsed;
                UpdatePlaybackVisuals();
            };
            _viewer.Activate();
        }
        catch
        {
            _media.SetMediaPlayer(_player);
            UpdatePlaybackVisuals();
            ShowFailure("放大窗口暂时无法打开，请重试。");
        }
    }

    private void Player_MediaOpened(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed || !ReferenceEquals(sender, _player)) return;
        if (_playRequested) sender.Play();
        ApplyPendingSeek();
        UpdatePlaybackVisuals();
    });

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed || !ReferenceEquals(sender, _player)) return;
        ShowFailure("无法播放此视频，文件可能损坏或缺少相应解码器。点击重试。");
    });

    private void Player_MediaEnded(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed || !ReferenceEquals(sender, _player)) return;
        _playRequested = false;
        _pendingSeekRatio = null;
        _item.IsPlaying = false;
        sender.Pause();
        sender.PlaybackSession.Position = TimeSpan.Zero;
        UpdatePlaybackVisuals();
    });

    private void PlaybackStateChanged(MediaPlaybackSession sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed || _player == null || !ReferenceEquals(sender, _player.PlaybackSession)) return;
        if (sender.PlaybackState == MediaPlaybackState.Playing)
        {
            _playRequested = true;
            _playbackRequested(this);
        }
        else if (sender.PlaybackState == MediaPlaybackState.Paused) _playRequested = false;
        UpdatePlaybackVisuals();
    });

    private void Session_PositionChanged(MediaPlaybackSession sender, object args) => DispatcherQueue.TryEnqueue(UpdateSeekBar);

    private void UpdatePlaybackVisuals()
    {
        if (_player == null || _failed) return;
        var state = _player.PlaybackSession.PlaybackState;
        var loading = state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
        _loading.IsActive = loading && _viewer == null;
        _loading.Visibility = _loading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _item.IsPlaying = state == MediaPlaybackState.Playing;
        UpdateSeekBar();
        if (_viewer != null) return;
        var opened = _player.PlaybackSession.NaturalVideoWidth > 0;
        _media.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;
        _poster.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
        // The play affordance stays available: before the first open, and whenever
        // playback is paused, so resuming is always one obvious tap away.
        var showPlay = !loading && (!opened || state == MediaPlaybackState.Paused);
        _playButton.Visibility = showPlay ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSeekBar()
    {
        if (_disposed) return;
        var session = _player?.PlaybackSession;
        var total = session?.NaturalDuration.TotalSeconds ?? 0;
        var position = session?.Position.TotalSeconds ?? 0;
        var ratio = total > 0 ? Math.Clamp(position / total, 0.0, 1.0) : Math.Clamp(_pendingSeekRatio ?? 0, 0.0, 1.0);
        if (_seekTrack.ActualWidth > 0)
        {
            var width = _seekTrack.ActualWidth;
            _seekFill.Width = ratio * width;
            _seekThumb.Margin = new Thickness(Math.Clamp(ratio * width - 5.5, 0, Math.Max(0, width - 11)), 0, 0, 0);
        }
        _positionText.Text = total > 0 ? FormatDuration(TimeSpan.FromSeconds(position)) : "0:00";
        _totalText.Text = total > 0 ? FormatDuration(TimeSpan.FromSeconds(total)) : FormatDuration(_item.VideoDuration);
        _durationText.Text = _totalText.Text;
    }

    private void ApplyPendingSeek()
    {
        if (_player?.PlaybackSession is not { } session || _pendingSeekRatio is not double ratio) return;
        if (session.PlaybackState == MediaPlaybackState.Opening || session.NaturalDuration.TotalSeconds <= 0) return;
        session.Position = TimeSpan.FromSeconds(ratio * session.NaturalDuration.TotalSeconds);
        _pendingSeekRatio = null;
    }

    private void SeekRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_seekRow).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (_viewer != null || _disposed) return;
        MarkActive();
        if (!_playRequested || _failed) StartPlayback();
        if (_player == null) return;
        _seekDragging = true;
        SetSeekHover(true);
        if (!_seekRow.CapturePointer(e.Pointer)) { _seekDragging = false; return; }
        SeekToPointer(e);
    }

    private void SeekRow_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_seekDragging) return;
        e.Handled = true;
        if (!e.GetCurrentPoint(_seekRow).Properties.IsLeftButtonPressed) { EndSeekDrag(); return; }
        SeekToPointer(e);
    }

    private void SeekRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_seekDragging) return;
        SeekToPointer(e);
        EndSeekDrag();
        e.Handled = true;
    }

    private void SeekRow_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _seekDragging = false;

    private void EndSeekDrag()
    {
        _seekDragging = false;
        try { _seekRow.ReleasePointerCaptures(); } catch { /* already released */ }
    }

    private void SeekToPointer(PointerRoutedEventArgs e)
    {
        if (_player?.PlaybackSession is not { } session) return;
        var width = _seekTrack.ActualWidth;
        if (width <= 0) return;
        var ratio = Math.Clamp(e.GetCurrentPoint(_seekTrack).Position.X / width, 0.0, 1.0);
        _pendingSeekRatio = ratio;
        ApplyPendingSeek();
        _seekFill.Width = ratio * width;
        _seekThumb.Margin = new Thickness(Math.Clamp(ratio * width - 5.5, 0, Math.Max(0, width - 11)), 0, 0, 0);
        _positionText.Text = FormatDuration(TimeSpan.FromSeconds(ratio * session.NaturalDuration.TotalSeconds));
    }

    private void ShowStatus(string message)
    {
        _statusText.Text = message;
        _statusChip.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string message)
    {
        _failed = true;
        Pause();
        _viewer?.ShowError(message);
        _loading.IsActive = false;
        _loading.Visibility = Visibility.Collapsed;
        _media.Visibility = Visibility.Collapsed;
        _poster.Visibility = Visibility.Visible;
        _playButton.Visibility = Visibility.Visible;
        AutomationProperties.SetName(_playButton, "重试播放视频");
        ShowStatus(message);
    }

    private void ReleasePlayer()
    {
        if (_player == null) return;
        _player.MediaOpened -= Player_MediaOpened;
        _player.MediaFailed -= Player_MediaFailed;
        _player.MediaEnded -= Player_MediaEnded;
        _player.PlaybackSession.PlaybackStateChanged -= PlaybackStateChanged;
        _player.PlaybackSession.PositionChanged -= Session_PositionChanged;
        _media.SetMediaPlayer(null);
        _player.Pause(); _player.Source = null;
        _player.Dispose(); _player = null;
        _source?.Dispose(); _source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tapTimer.Stop();
        ThemeService.Changed -= ApplyCardTheme;
        _viewer?.Close(); _viewer = null;
        _item.IsPlaying = false;
        ReleasePlayer();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
