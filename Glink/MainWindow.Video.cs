using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Glink.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Glink;

public sealed partial class MainWindow
{
    private readonly List<VideoCardView> _videoCards = new();
    // 空格的落点：最近一次交互/播放的可播放元素（VideoCardView 或音频 ItemVM）。
    private object? _lastPlayable;
    private bool _isClosed;

    private FrameworkElement BuildVideoCard(ItemVM item)
    {
        var card = new VideoCardView(item, AppWindow, VideoPlaybackRequested, surfaceWidth: 478, activated: OnPlayableActivated);
        _videoCards.Add(card);
        return card;
    }

    private void OnPlayableActivated(VideoCardView card) => _lastPlayable = card;

    private static async Task LoadVideoPreviewAsync(ItemVM item)
    {
        if (string.IsNullOrEmpty(item.FilePath)) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Store.AbsolutePath(item.FilePath));
            var properties = await file.Properties.GetVideoPropertiesAsync();
            var rotated = properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270;
            var width = rotated ? properties.Height : properties.Width;
            var height = rotated ? properties.Width : properties.Height;
            if (width > 0 && height > 0) item.VideoAspectRatio = (double)width / height;
            item.VideoDuration = properties.Duration;
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 800, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail?.Type != ThumbnailType.Image) return;
            var bitmap = new BitmapImage { DecodePixelWidth = 800 };
            await bitmap.SetSourceAsync(thumbnail);
            item.ImageSource = bitmap;
        }
        catch (Exception error)
        {
            // Missing thumbnails/codecs do not prevent importing or showing a playable card.
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    private void VideoPlaybackRequested(VideoCardView active)
    {
        if (_isClosed) return;
        _mediaPlayer.Pause();
        _progressTimer.Stop();
        if (_playingItem != null) _playingItem.IsPlaying = false;
        foreach (var video in _videoCards)
            if (!ReferenceEquals(active, video)) video.Pause();
        _lastPlayable = active;
    }

    // 空格的落点：最近一次交互/播放的可播放元素。焦点所在的视频卡只有在
    // 它正是最近激活对象（或尚无任何登记）时才生效——点击音频波形不会
    // 移动键盘焦点，若仍让滞留焦点的视频卡优先，空格会错误地控制视频。
    // 独立播放窗口打开时键盘属于那个窗口，这里不动它的卡片。
    private void ToggleActivePlayback()
    {
        VideoCardView? focused = null;
        for (var node = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
             node != null;
             node = VisualTreeHelper.GetParent(node))
            if (node is VideoCardView card) { focused = card; break; }
        if (focused != null && (_lastPlayable == null || ReferenceEquals(_lastPlayable, focused)))
        {
            if (!focused.IsViewerOpen) focused.TogglePlayback();
            return;
        }
        switch (_lastPlayable)
        {
            // 视频卡必须仍在当前视图（防跨分类重载后的陈旧引用）。
            case VideoCardView video when !video.IsViewerOpen && _videoCards.Contains(video):
                video.TogglePlayback();
                break;
            // 音频只在它还显示在当前视图时才响应，避免切走分类后"看不见却在播"。
            case ItemVM audio when audio.Type == ItemType.Audio && IsCardOnView(audio):
                ToggleMediaPlayback(audio);
                break;
        }
    }

    private bool IsCardOnView(ItemVM vm) =>
        (_isListMode ? _listCards.Cast<FrameworkElement>() : CardCanvas.Children.Cast<FrameworkElement>())
        .Any(child => child.DataContext is ItemVM item && item.Id == vm.Id);

    private void PauseVideos()
    {
        foreach (var video in _videoCards) video.Pause();
    }

    private void DisposeVideoCards()
    {
        foreach (var video in _videoCards) video.Dispose();
        _videoCards.Clear();
    }

    private void ReleaseVideoForItem(Guid id)
    {
        foreach (var video in _videoCards.Where(v => v.ItemId == id).ToArray())
        {
            video.Dispose();
            _videoCards.Remove(video);
        }
    }
}
