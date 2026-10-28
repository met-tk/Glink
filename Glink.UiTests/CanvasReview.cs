using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Glink.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glink;

// Run: python Glink.UiTests/seed.py .review-automated/.review-data
// Build Glink with -p:CanvasReview=true into a separate output directory.
// WinUI needs a trailing slash on the absolute -o path to copy XBF resources correctly.
// Start with GLINK_DATA_ROOT pointing to a seeded, disposable .review-data directory.
// Set GLINK_DIALOG_REVIEW=1 to run only the real dialog theme/state checks and PNG capture.
// Set GLINK_SETTINGS_REVIEW=1 to run only the settings/theme-picker checks and PNG capture.
// Uses the real WinUI dispatcher/layout/ScrollViewer; no mouse input is injected.
public sealed partial class MainWindow
{
    internal async Task RunCanvasReviewAsync()
    {
        var log = new List<string>();
        if (Path.GetFileName(Store.DataDirectory) != ".review-data") return;
        if (Environment.GetEnvironmentVariable("GLINK_EXPORT_REVIEW") == "1")
        {
            await RunExportReviewAsync();
            return;
        }
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, e) =>
            File.WriteAllText(Path.Combine(Store.DataDirectory, "ui-unhandled.txt"), e.Message + "\n" + e.Exception);
        if (Environment.GetEnvironmentVariable("GLINK_SETTINGS_REVIEW") == "1")
        {
            await RunSettingsReviewAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("GLINK_DIALOG_REVIEW") == "1")
        {
            await RunDialogThemeReviewAsync();
            return;
        }
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            log.Add("PASS: " + name);
        }
        void CheckLayout(string name)
        {
            var cards = CardCanvas.Children.OfType<Border>().ToList();
            var rects = cards.Select(c => new Rect(Canvas.GetLeft(c), Canvas.GetTop(c), c.ActualWidth, c.ActualHeight)).ToList();
            Check(rects.Count > 0 && rects.All(r => r.X >= 0 && r.Y >= 0 && r.Right <= Math.Max(360, CompactLayoutWidth()) + 1), name + " fits viewport");
            Check(rects.SelectMany((a, i) => rects.Skip(i + 1).Select(b =>
                a.X >= b.Right || a.Right <= b.X || a.Y >= b.Bottom || a.Bottom <= b.Y)).All(x => x), name + " has no overlap");
        }
        async Task Reload()
        {
            await RefreshItemsAsync();
            await Task.Delay(200);
        }
        try
        {
            if (Path.GetFileName(Store.DataDirectory) != ".review-data")
                throw new InvalidOperationException("A disposable .review-data directory is required.");
            await _storeInitialization;
            await Task.Delay(900);
            SettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            foreach (var theme in ThemeService.Themes.Concat(ThemeService.Themes.Reverse()))
            {
                SelectTheme(theme);
                await Task.Delay(180);
                Check(ThemeService.Current == theme, "Theme preset " + theme.Name);
            }
            SettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            Check(File.ReadAllText(Path.Combine(Store.DataDirectory, "appearance.txt")) == ThemeService.Current.Id, "Theme selection is persisted");
            _selectedCategoryId = null; _selectedType = null; _selectedTimeFilter = null;
            await Reload();
            var originals = (await Store.GetItemsAsync(null, null, null, false)).ToDictionary(i => i.Id, i => (i.PosX, i.PosY, i.ZIndex));
            Check(CardCanvas.Children.Count >= 6, "Seed library loaded");
            Check(ViewWordCloudButton.Visibility == Visibility.Collapsed, "Word cloud is unavailable outside text filtering");
            CheckLayout("All items");
            Check(CardCanvas.Children.OfType<Border>().Min(Canvas.GetLeft) == 0 && CardCanvas.Children.OfType<Border>().Min(Canvas.GetTop) == 0, "Old offscreen coordinates are ignored");
            foreach (var category in _categories)
            {
                _selectedCategoryId = category.Id;
                await Reload();
                CheckLayout("Category " + category.Name);
            }
            _selectedCategoryId = null; _selectedType = ItemType.Text;
            await Reload(); CheckLayout("Text filter");
            _selectedTimeFilter = "earlier";
            await Reload(); CheckLayout("Earlier text filter");
            _selectedType = null; _selectedTimeFilter = null;
            await Reload();

            CanvasScroll.ChangeView(1200, 800, 1, true);
            await Task.Delay(250);
            var anchor = new Point(400, 280);
            var contentPoint = CanvasScroll.TransformToVisual(CanvasHost).TransformPoint(anchor);
            ZoomCanvas(2, anchor);
            await Task.Delay(250);
            Check(Math.Abs(CanvasScroll.ZoomFactor - 2) < .001, "Native canvas zooms to 200%");
            var after = CanvasHost.TransformToVisual(CanvasScroll).TransformPoint(contentPoint);
            Check(Math.Abs(after.X - anchor.X) < 2 && Math.Abs(after.Y - anchor.Y) < 2, $"Zoom keeps pointer anchor ({after.X:F1}, {after.Y:F1})");
            ApplyCompactLayout(); await Task.Delay(150); CheckLayout("Compact at 200%");
            ZoomCanvas(.5, anchor); await Task.Delay(250);
            Check(Math.Abs(CanvasScroll.ZoomFactor - .5) < .001, "Native canvas zooms out to 50%");
            ApplyCompactLayout(); await Task.Delay(150); CheckLayout("Compact at 50%");
            ZoomCanvas(.01, anchor); await Task.Delay(200);
            Check(Math.Abs(CanvasScroll.ZoomFactor - .25) < .001, "Minimum zoom is 25%");
            ZoomCanvas(10, anchor); await Task.Delay(200);
            Check(Math.Abs(CanvasScroll.ZoomFactor - 2.5) < .001, "Maximum zoom is 250%");
            ZoomReset_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await Task.Delay(200);
            Check(Math.Abs(CanvasScroll.ZoomFactor - 1) < .001, "Reset restores 100%");

            await ReviewTextListAsync(Check);

            _selectedType = ItemType.Audio; await Reload();
            var audio = CardCanvas.Children.OfType<Border>().Single();
            var vm = (ItemVM)audio.DataContext;
            var waveform = FindWaveformCanvasForItem(vm)!;
            Check(waveform.Background != null, "Waveform gaps receive pointer input");
            var center = waveform.TransformToVisual(audio).TransformPoint(new Point(waveform.ActualWidth / 2, 4));
            Check(!IsCardMoveHandle(audio, center) && !IsCardMoveHandle(audio, new Point(audio.ActualWidth / 2, audio.ActualHeight / 2)), "Waveform and card interior cannot initiate a move");
            Check(IsCardMoveHandle(audio, new Point(8, 40)) && IsCardMoveHandle(audio, new Point(180, 8)) &&
                  IsCardMoveHandle(audio, new Point(audio.ActualWidth - 8, 40)) && IsCardMoveHandle(audio, new Point(180, audio.ActualHeight - 8)), "All four audio edges are move handles");
            ToggleMediaPlayback(vm);
            _pendingSeekRatio = .7;
            await Task.Delay(700);
            ApplyPendingAudioSeek();
            Check(Math.Abs(_mediaPlayer.PlaybackSession.Position.TotalSeconds - _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds * .7) < 2, "First audio seek survives media loading");
            _mediaPlayer.Pause(); _progressTimer.Stop();

            ZoomCanvas(.5, anchor); await Task.Delay(200);
            _pendingDragCard = audio; _pendingDragMoved = true;
            await CreateDragPreviewAsync(audio, ++_dragGeneration);
            Check(_dragPreview != null && Math.Abs(_dragPreview.Width - audio.ActualWidth * .5) < .1, "Drag preview follows canvas zoom");
            CancelCanvasDrag();

            ZoomCanvas(1, anchor); await Task.Delay(150);
            _selectedType = ItemType.Video; await Reload();
            CheckLayout("Video filter");
            var video = _videoCards.Single();
            Check(video.ReviewItem.VideoDuration > TimeSpan.Zero && video.ReviewItem.VideoAspectRatio > 0, "Video metadata is loaded");
            Check(video.ReviewItem.ImageSource != null, "Video card has a real thumbnail");
            video.TogglePlayback();
            for (var attempt = 0; attempt < 50 && video.ReviewPlayer?.PlaybackSession.NaturalVideoWidth is not > 0; attempt++) await Task.Delay(100);
            Check(video.ReviewPlayer?.PlaybackSession.NaturalVideoWidth > 0 && video.ReviewMedia.Visibility == Microsoft.UI.Xaml.Visibility.Visible, "Video decodes into the inline visual surface");
            var player = video.ReviewPlayer!;
            Check(ReferenceEquals(video.ReviewMedia.MediaPlayer, player), "Inline video is bound to its player");
            var colorCategory = _categories.Single(c => c.Id == video.ReviewItem.CategoryId);
            var oldColor = colorCategory.AccentHex;
            var newColor = oldColor == "#7CACE2" ? "#CE83AC" : "#7CACE2";
            await ChangeCategoryColorAsync(colorCategory, newColor);
            var videoBorder = (Border)video.Parent;
            Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)videoBorder.BorderBrush).Color == CategoryVM.HexToColor(newColor), "Category color immediately updates the video card border");
            Check(ReferenceEquals(video.ReviewPlayer, player), "Changing category color preserves the active video player");
            await _history.UndoAsync();
            Check(colorCategory.AccentHex == oldColor && ((Microsoft.UI.Xaml.Media.SolidColorBrush)videoBorder.BorderBrush).Color == CategoryVM.HexToColor(oldColor), "Undo category color updates both sidebar and card");
            await _history.RedoAsync();
            Check(colorCategory.AccentHex == newColor, "Redo category color reapplies the chosen color");
            await _history.UndoAsync();
            video.Pause(); await Task.Delay(150);
            Check(player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused, "Video pauses");
            var seek = player.PlaybackSession.NaturalDuration.TotalSeconds * .35;
            player.PlaybackSession.Position = TimeSpan.FromSeconds(seek); await Task.Delay(150);
            Check(Math.Abs(player.PlaybackSession.Position.TotalSeconds - seek) < .15, "Video seeks while paused");
            video.OpenViewer(); await Task.Delay(250);
            var viewer = video.ReviewViewer!;
            Check(viewer != null && ReferenceEquals(viewer.ReviewMedia.MediaPlayer, player) && video.ReviewMedia.MediaPlayer == null, "Enlarged viewer owns the same video player");
            Check(Math.Abs(player.PlaybackSession.Position.TotalSeconds - seek) < .15 && player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused, "Enlarging preserves paused state and playback position");
            var expectedX = AppWindow.Position.X + AppWindow.Size.Width / 2;
            var expectedY = AppWindow.Position.Y + AppWindow.Size.Height / 2;
            Check(Math.Abs(viewer!.AppWindow.Position.X + viewer.AppWindow.Size.Width / 2 - expectedX) <= 2 &&
                  Math.Abs(viewer.AppWindow.Position.Y + viewer.AppWindow.Size.Height / 2 - expectedY) <= 2, "Video window is centered on the main window");
            video.OpenViewer();
            Check(ReferenceEquals(video.ReviewViewer, viewer), "Repeated enlarge reuses the same window");
            foreach (var theme in ThemeService.Themes) { SelectTheme(theme); await Task.Delay(120); }
            Check(ThemeService.Current == ThemeService.Themes.Last(), "Theme changes are safe with video window open");
            viewer.Close(); await Task.Delay(200);
            Check(video.ReviewViewer == null && ReferenceEquals(video.ReviewMedia.MediaPlayer, player), "Closing enlarged video restores the inline surface");
            Check(Math.Abs(player.PlaybackSession.Position.TotalSeconds - seek) < .15, "Closing enlarged video preserves progress");
            video.TogglePlayback(); await Task.Delay(180);
            Check(player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing, "Video resumes after returning to canvas");
            ToggleMediaPlayback(vm); await Task.Delay(200);
            Check(player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused, "Playing audio pauses video");
            video.TogglePlayback(); await Task.Delay(180);
            Check(_mediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused, "Playing video pauses audio");
            player.PlaybackSession.Position = player.PlaybackSession.NaturalDuration - TimeSpan.FromMilliseconds(200);
            await Task.Delay(500);
            Check(!video.ReviewItem.IsPlaying && player.PlaybackSession.Position.TotalSeconds < .15, "Video end resets replay position");
            video.TogglePlayback(); await Task.Delay(150);
            Check(player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing, "Ended video can replay");
            video.OpenViewer(); await Task.Delay(150);
            _selectedType = ItemType.Text; await Reload();
            Check(video.ReviewPlayer == null && video.ReviewViewer == null, "Changing view closes the viewer and releases video resources");

            using (var missing = new VideoCardView(new ItemVM { Type = ItemType.Video, FilePath = "media/missing.mp4" }, AppWindow, _ => { }))
            {
                missing.TogglePlayback();
                Check(missing.ReviewFailed, "Missing video shows a recoverable error");
            }
            var brokenPath = Path.Combine(Store.DataDirectory, "media", "broken.mp4");
            await File.WriteAllTextAsync(brokenPath, "invalid video fixture");
            using (var broken = new VideoCardView(new ItemVM { Type = ItemType.Video, FilePath = "media/broken.mp4" }, AppWindow, _ => { }))
            {
                broken.TogglePlayback();
                for (var attempt = 0; attempt < 50 && !broken.ReviewFailed; attempt++) await Task.Delay(100);
                Check(broken.ReviewFailed && !broken.ReviewItem.IsPlaying, "Corrupt video fails without crashing or remaining in a playing state");
            }
            var current = await Store.GetItemsAsync(null, null, null, false);
            Check(current.All(i => originals[i.Id] == (i.PosX, i.PosY, i.ZIndex)), "View-specific packing never rewrites stored positions");
            _selectedType = ItemType.Text; _favoritesOnly = false; await Reload();
            var textCard = CardCanvas.Children.OfType<Border>().First();
            var textItem = (ItemVM)textCard.DataContext;
            var oldText = textItem.Content;
            StartInlineEdit(textItem);
            _activeEditBox!.Text = "撤回验证 " + Guid.NewGuid() + "\n日本語 🎵";
            var editedValue = _activeEditBox.Text; // WinUI normalizes newlines to CR.
            EndInlineEdit(true); await _textSaveTask;
            await _history.UndoAsync();
            Check((await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == textItem.Id).Content == oldText, "Undo restores a saved inline text edit");
            await _history.RedoAsync();
            Check((await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == textItem.Id).Content == editedValue, "Redo restores the full Unicode text edit");
            await _history.UndoAsync();
            textCard = CardCanvas.Children.OfType<Border>().First();
            textItem = (ItemVM)textCard.DataContext;
            var favoriteBefore = textItem.IsFavorite;
            FavoriteItem_Click(new MenuFlyoutItem { DataContext = textItem }, new Microsoft.UI.Xaml.RoutedEventArgs());
            await Task.Delay(250);
            _favoritesOnly = true; _selectedCategoryId = null; _selectedType = null; await Reload();
            Check(CardCanvas.Children.OfType<Border>().All(c => ((ItemVM)c.DataContext).IsFavorite) && ContentTitle.Text == "收藏", "Favorites navigation displays only favorited items");
            Check(int.Parse(FavoritesCountText.Text) == await Store.GetItemCountAsync("favorites"), "Favorites sidebar count stays in sync");
            await _history.UndoAsync();
            Check((await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == textItem.Id).IsFavorite == favoriteBefore, "Undo favorite restores collection membership");
            _favoritesOnly = false; await Reload();
            var displaced = CardCanvas.Children.OfType<Border>().First();
            Canvas.SetLeft(displaced, 900); Canvas.SetTop(displaced, 700);
            Pack_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await Task.Delay(400);
            await _history.UndoAsync();
            await Task.Delay(400);
            Check(Canvas.GetLeft(displaced) == 900 && Canvas.GetTop(displaced) == 700, "Undo compact tiling restores the prior arrangement");
            await _history.RedoAsync();
            CheckLayout("Redo compact tiling");
            log.Add($"All {log.Count} canvas checks passed.");
        }
        catch (Exception error) { log.Add("FAIL: " + error); }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(Store.DataDirectory, "canvas-review.txt"), log);
            Close();
        }
    }

    private async Task RunSettingsReviewAsync()
    {
        var log = new List<string>();
        var previousTheme = ThemeService.Current;
        var previousSize = AppWindow.Size;
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var wasMaximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            log.Add("PASS: " + message);
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        async Task Settle()
        {
            await Task.Delay(240);
            RootGrid.UpdateLayout();
        }
        async Task TogglePicker(bool expanded)
        {
            if (ThemePickerToggle.IsChecked == expanded) return;
            var peer = new ToggleButtonAutomationPeer(ThemePickerToggle);
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
            await Settle();
        }
        async Task Capture(string name)
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(SettingsPanel);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[checked((int)pixels.Length)];
            reader.ReadBytes(bytes);
            var folder = await StorageFolder.GetFolderFromPathAsync(Store.DataDirectory);
            var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            Check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Captured " + name);
        }
        try
        {
            await _storeInitialization;
            await Task.Delay(900);
            presenter?.Restore();
            var scale = RootGrid.XamlRoot.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)Math.Round(1180 * scale), (int)Math.Round(820 * scale)));
            SettingsButton_Click(this, new RoutedEventArgs());
            await Settle();
            Check(SettingsPanel.Visibility == Visibility.Visible, "Settings opens through the real action");
            Check(ThemePickerToggle.IsChecked != true && ThemeOptions.Visibility == Visibility.Collapsed,
                "Theme picker starts unchecked and collapsed");
            Check(CurrentThemeName.Text == ThemeService.Current.Name, "Collapsed picker names the current theme");
            Check(ThemeSaveStatus.Visibility == Visibility.Collapsed, "Theme error status starts collapsed");
            Check(VersionText.Text == "1.1.1", "About shows only version 1.1.1");
            Check(CreatorLink.Content as string == "AI-driven by taketo" &&
                CreatorLink.NavigateUri?.AbsoluteUri == "https://space.bilibili.com/24340298",
                "Creator is a hyperlink to the requested profile");
            var settingsScroll = Descendants(SettingsPanel).OfType<ScrollViewer>().First();
            bool IsInSettingsScroll(DependencyObject element)
            {
                for (var node = VisualTreeHelper.GetParent(element); node != null && node != SettingsPanel; node = VisualTreeHelper.GetParent(node))
                    if (node == settingsScroll) return true;
                return false;
            }
            Check(!IsInSettingsScroll(VersionText) && !IsInSettingsScroll(CreatorLink), "About information lives outside the scrolling settings content");
            var footerY = CreatorLink.TransformToVisual(SettingsPanel).TransformPoint(new Point()).Y;
            Check(footerY > SettingsPanel.ActualHeight / 2 && footerY + CreatorLink.ActualHeight <= SettingsPanel.ActualHeight,
                "About information is visible at the panel bottom");
            var collapsedCanvasY = CanvasOptions.TransformToVisual(settingsScroll.Content as UIElement).TransformPoint(new Point()).Y;
            var collapsedExtent = settingsScroll.ExtentHeight;
            await TogglePicker(true);
            Check(ThemePickerToggle.IsChecked == true && ThemeOptions.Visibility == Visibility.Visible && ThemeOptions.ActualHeight > 150,
                "Native toggle expands the theme palette");
            var expandedCanvasY = CanvasOptions.TransformToVisual(settingsScroll.Content as UIElement).TransformPoint(new Point()).Y;
            Check(expandedCanvasY > collapsedCanvasY + 100 && settingsScroll.ExtentHeight > collapsedExtent + 100,
                "Expanded palette takes layout space that the collapsed picker releases");
            var themeButtons = Descendants(ThemeOptions).OfType<Button>().Where(b => b.Tag is MoodTheme).ToList();
            Check(ThemeService.Themes.Count == 12 && themeButtons.Count == 12 && _themeChips.Count == 12,
                "All twelve themes have selectable palette chips");
            foreach (var button in themeButtons)
            {
                var theme = (MoodTheme)button.Tag;
                var peer = new ButtonAutomationPeer(button);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Settle();
                Check(ThemeService.Current == theme && CurrentThemeName.Text == theme.Name &&
                    RootGrid.ActualTheme == (theme.IsLight ? ElementTheme.Light : ElementTheme.Dark),
                    "Selecting " + theme.Id + " updates the preset, current name and element theme");
                var selected = _themeChips.Single(c => c.Theme.Id == theme.Id);
                Check(selected.Ring.BorderBrush is SolidColorBrush ring && ring.Color == CategoryVM.HexToColor(theme.Accent),
                    theme.Id + " has the selected accent ring");
                Check(File.ReadAllText(Path.Combine(Store.DataDirectory, "appearance.txt")).Trim() == theme.Id &&
                    ThemeSaveStatus.Visibility == Visibility.Collapsed, theme.Id + " persists without showing a success or error footer");
            }
            foreach (var themeId in new[] { "twilight", "celadon" })
            {
                SelectTheme(ThemeService.Themes.Single(t => t.Id == themeId));
                await Settle();
                settingsScroll.ChangeView(null, 0, null, true);
                await TogglePicker(false);
                await Settle();
                Check(ThemeOptions.Visibility == Visibility.Collapsed &&
                    Math.Abs(CanvasOptions.TransformToVisual(settingsScroll.Content as UIElement).TransformPoint(new Point()).Y - collapsedCanvasY) < 1,
                    themeId + " collapses without leaving palette space");
                await Capture("settings-" + themeId + "-collapsed");
                await TogglePicker(true);
                await Capture("settings-" + themeId + "-expanded");
                var beforeScrollY = CreatorLink.TransformToVisual(SettingsPanel).TransformPoint(new Point()).Y;
                settingsScroll.ChangeView(null, settingsScroll.ScrollableHeight, null, true);
                await Settle();
                Check(settingsScroll.VerticalOffset > 0 &&
                    Math.Abs(CreatorLink.TransformToVisual(SettingsPanel).TransformPoint(new Point()).Y - beforeScrollY) < 1 &&
                    Math.Abs(beforeScrollY - footerY) < 1, themeId + " keeps About fixed while settings scroll");
                settingsScroll.ChangeView(null, 0, null, true);
                await Settle();
            }
            await TogglePicker(false);
            CloseSettings_Click(this, new RoutedEventArgs());
            await Settle();
            Check(SettingsPanel.Visibility == Visibility.Collapsed, "Settings closes through the real action");
            log.Add($"All {log.Count} settings checks passed.");
        }
        catch (Exception error) { log.Add("FAIL: " + error); }
        finally
        {
            SelectTheme(previousTheme);
            AppWindow.Resize(previousSize);
            if (wasMaximized) presenter?.Maximize();
            await Settle();
            await File.WriteAllLinesAsync(Path.Combine(Store.DataDirectory, "settings-review.txt"), log);
            Close();
        }
    }

    private async Task RunDialogThemeReviewAsync()
    {
        var log = new List<string>();
        var failures = 0;
        var checks = 0;
        var previousTheme = ThemeService.Current;
        void Check(bool result, string message)
        {
            checks++;
            if (!result) failures++;
            log.Add((result ? "PASS: " : "FAIL: ") + message);
        }
        static string ColorOf(Brush? brush) => brush is SolidColorBrush solid
            ? $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : brush?.GetType().Name ?? "null";
        static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root is T found) return found;
            if (root == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
            return null;
        }
        async Task<ContentDialog> WaitForDialog()
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot)
                    .Select(p => FindDescendant<ContentDialog>(p.Child)).FirstOrDefault(d => d != null);
                if (dialog != null) { await Task.Delay(150); dialog.UpdateLayout(); return dialog; }
                await Task.Delay(50);
            }
            throw new InvalidOperationException("The real ContentDialog did not open.");
        }
        async Task Capture(FrameworkElement element, string name)
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[checked((int)pixels.Length)];
            reader.ReadBytes(bytes);
            var folder = await StorageFolder.GetFolderFromPathAsync(Store.DataDirectory);
            var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            Check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Captured " + name);
        }
        try
        {
            await _storeInitialization;
            await Task.Delay(900);
            _selectedCategoryId = null; _selectedType = null; _selectedTimeFilter = null; _favoritesOnly = false;
            await RefreshItemsAsync();
            var beforeItems = (await Store.GetItemsAsync(null, null, null, false)).ToDictionary(i => i.Id, i => i.Content);
            if (beforeItems.Count == 0) throw new InvalidOperationException("Seed data is required for the save-layout dialog.");
            var beforePresets = _layoutPresets.Count;
            foreach (var theme in ThemeService.Themes)
            {
                SelectTheme(theme);
                await Task.Delay(220);
                foreach (var kind in new[] { "text", "layout" })
                {
                    ContentDialog? dialog = null;
                    var context = theme.Id + "/" + kind;
                    try
                    {
                        if (kind == "text") AddText_Click(this, new RoutedEventArgs());
                        else SaveLayout_Click(this, new RoutedEventArgs());
                        dialog = await WaitForDialog();
                        var input = dialog.Content as TextBox ?? throw new InvalidOperationException("Dialog content is not a TextBox.");
                        var primary = FindChildByName<Button>(dialog, "PrimaryButton") ?? throw new InvalidOperationException("PrimaryButton is missing.");
                        var close = FindChildByName<Button>(dialog, "CloseButton") ?? throw new InvalidOperationException("CloseButton is missing.");
                        var background = FindChildByName<Border>(dialog, "BackgroundElement") ?? throw new InvalidOperationException("Dialog BackgroundElement is missing.");
                        var inputBorder = FindChildByName<Border>(input, "BorderElement") ?? throw new InvalidOperationException("TextBox BorderElement is missing.");
                        var inputContent = FindChildByName<ScrollViewer>(input, "ContentElement") ?? throw new InvalidOperationException("TextBox ContentElement is missing.");
                        var placeholder = FindChildByName<TextBlock>(input, "PlaceholderTextContentPresenter") ?? throw new InvalidOperationException("TextBox placeholder is missing.");
                        var expectedTheme = theme.IsLight ? ElementTheme.Light : ElementTheme.Dark;
                        Check(new FrameworkElement[] { dialog, input, primary, close }.All(e => e.ActualTheme == expectedTheme),
                            context + $" actual themes dialog={dialog.ActualTheme}, input={input.ActualTheme}, primary={primary.ActualTheme}, close={close.ActualTheme}; expected={expectedTheme}");

                        void Colors(string part, params (string Property, Brush? Actual, string Key)[] values)
                        {
                            var matches = true;
                            var diagnostic = new List<string>();
                            foreach (var (property, actual, key) in values)
                            {
                                var expected = dialog.Resources.TryGetValue(key, out var resource) ? resource as Brush : null;
                                var actualColor = ColorOf(actual);
                                var expectedColor = ColorOf(expected);
                                matches &= actual is SolidColorBrush && expected is SolidColorBrush && actualColor == expectedColor;
                                diagnostic.Add($"{property}={actualColor}, expected={expectedColor} ({key})");
                            }
                            Check(matches, context + " " + part + ": " + string.Join("; ", diagnostic));
                        }
                        Colors("dialog surface", ("background", background.Background, "ContentDialogBackground"),
                            ("border", background.BorderBrush, "ContentDialogBorderBrush"));
                        input.Focus(FocusState.Programmatic);
                        input.Text = kind == "text" ? "留住此刻的灵感。\n中文 · 日本語 · English" : "周末旅行的灵感布局";

                        // Native TextBox has no Pressed state; buttons have no Focused brush state.
                        foreach (var state in new[] { "Normal", "PointerOver", "Focused", "Disabled" })
                        {
                            Check(VisualStateManager.GoToState(input, state, false), context + " TextBox enters " + state);
                            await Task.Delay(110);
                            var suffix = state == "Normal" ? "" : state;
                            Colors("TextBox " + state,
                                ("background", inputBorder.Background, "TextControlBackground" + suffix),
                                ("border", inputBorder.BorderBrush, "TextControlBorderBrush" + suffix),
                                ("foreground", inputContent.Foreground, "TextControlForeground" + suffix),
                                ("placeholder", placeholder.Foreground, "TextControlPlaceholderForeground" + suffix));
                        }
                        VisualStateManager.GoToState(input, "Focused", false);
                        foreach (var (button, prefix, label) in new[] { (primary, "AccentButton", "save"), (close, "Button", "cancel") })
                        {
                            var presenter = FindChildByName<ContentPresenter>(button, "ContentPresenter")
                                ?? throw new InvalidOperationException(label + " ContentPresenter is missing.");
                            foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Disabled" })
                            {
                                Check(VisualStateManager.GoToState(button, state, false), context + " " + label + " enters " + state);
                                await Task.Delay(110); // The stock presenter has an 83ms background transition.
                                var suffix = state == "Normal" ? "" : state;
                                Colors(label + " " + state,
                                    ("background", presenter.Background, prefix + "Background" + suffix),
                                    ("border", presenter.BorderBrush, prefix + "BorderBrush" + suffix),
                                    ("foreground", presenter.Foreground, prefix + "Foreground" + suffix));
                            }
                            VisualStateManager.GoToState(button, "Normal", false);
                        }
                        await Task.Delay(110);
                        if (theme.Id is "celadon" or "twilight" or "ink" or "blossom")
                            await Capture(background, "dialog-" + theme.Id + "-" + kind);
                    }
                    catch (Exception error) { Check(false, context + " exception: " + error); }
                    finally
                    {
                        dialog?.Hide(); // Hide returns None; neither real dialog writes library/layout data.
                        await Task.Delay(220);
                        await File.WriteAllLinesAsync(Path.Combine(Store.DataDirectory, "dialog-review.txt"), log);
                    }
                }
            }
            var afterItems = await Store.GetItemsAsync(null, null, null, false);
            Check(afterItems.Count == beforeItems.Count && afterItems.All(i => beforeItems.TryGetValue(i.Id, out var text) && text == i.Content),
                "Closing test dialogs preserves all library items and text");
            Check(_layoutPresets.Count == beforePresets, "Closing layout dialogs creates no saved layouts");
        }
        catch (Exception error) { Check(false, "Dialog review exception: " + error); }
        finally
        {
            SelectTheme(previousTheme);
            await Task.Delay(150);
            log.Add($"Dialog review: {checks - failures}/{checks} checks passed; {failures} failed.");
            await File.WriteAllLinesAsync(Path.Combine(Store.DataDirectory, "dialog-review.txt"), log);
            Close();
        }
    }

    private async Task ReviewTextListAsync(Action<bool, string> check)
    {
        var previousSize = AppWindow.Size;
        var previousTheme = ThemeService.Current;
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var wasMaximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        presenter?.Restore();

        async Task Settle()
        {
            await Task.Delay(250);
            RootGrid.UpdateLayout();
        }
        async Task Capture(string name)
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(RootGrid);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[checked((int)pixels.Length)];
            reader.ReadBytes(bytes);
            var directory = await StorageFolder.GetFolderFromPathAsync(Store.DataDirectory);
            var file = await directory.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Captured " + name);
        }
        void CheckRowsFit(string name)
        {
            check(_listCards.All(row =>
            {
                var text = FindChildByName<TextBlock>(row, "ContentText")!;
                var meta = FindChildByName<TextBlock>(row, "ContentMeta")!;
                var textOrigin = text.TransformToVisual(row).TransformPoint(new Point());
                var metaOrigin = meta.TransformToVisual(row).TransformPoint(new Point());
                var rowOrigin = row.TransformToVisual(ListScroll).TransformPoint(new Point());
                return text.ActualWidth > 0 && textOrigin.X + text.ActualWidth <= metaOrigin.X + 1 &&
                    metaOrigin.X + meta.ActualWidth <= row.ActualWidth + 1 &&
                    rowOrigin.X + row.ActualWidth <= ListScroll.ActualWidth - ListScroll.Padding.Right + 1 &&
                    row.ActualHeight >= 47 && row.ActualHeight <= 56;
            }) && ListScroll.ScrollableWidth <= 1, name + " summaries and dates fit without horizontal scrolling");
        }
        static string NormalizeLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        try
        {
            _selectedType = ItemType.Text;
            ChipAllType.IsChecked = false; ChipText.IsChecked = true;
            await RefreshItemsAsync(); await Settle();
            var originalCards = CardCanvas.Children.OfType<Border>().ToList();
            check(originalCards.Count >= 4, "Text list has varied seed content");
            check(ViewWordCloudButton.Visibility == Visibility.Visible, "Text filtering reveals word cloud");
            var resized = originalCards.First();
            resized.Width = 287;
            ApplyCardWidthToContent(resized, resized.Width);
            var canvasState = originalCards.ToDictionary(c => ((ItemVM)c.DataContext).Id,
                c => (c.Width, c.MinHeight, c.Child));
            ZoomCanvas(.5, CanvasViewportCenter()); await Settle();
            ApplyAdaptiveTextScale();
            check(originalCards.Any(c => FindChildByName<TextBlock>(c, "ContentMeta") is { } meta &&
                (meta.Visibility != Visibility.Visible || meta.Opacity < .1)), "Low canvas zoom hides text metadata before switching views");
            SelectTheme(ThemeService.Themes.Single(t => t.Id == "paper"));
            SetViewMode("wordcloud"); await Settle();
            check(_isWordCloudMode && originalCards.All(c =>
            {
                var text = FindChildByName<TextBlock>(c, "ContentText")!;
                var meta = FindChildByName<TextBlock>(c, "ContentMeta")!;
                return c.Background is SolidColorBrush { Color.A: 0 } && c.BorderThickness == new Thickness(0) &&
                    c.Padding == new Thickness(8) && c.MinHeight == 0 && text.TextWrapping == TextWrapping.NoWrap &&
                    text.FontSize == 15 && text.LineHeight == 22 && meta.Visibility == Visibility.Collapsed;
            }), "Word cloud contains only full-size transparent text with a small margin");
            var cloudText = FindChildByName<TextBlock>(originalCards[0], "ContentText")!;
            cloudText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            check(Math.Abs(originalCards[0].Width - cloudText.DesiredSize.Width - 16) < 1 &&
                originalCards[0].ActualHeight <= cloudText.DesiredSize.Height + 17, "Word cloud card hugs its measured text");
            var cloudRects = originalCards.Select(c => new Rect(Canvas.GetLeft(c), Canvas.GetTop(c), c.ActualWidth, c.ActualHeight)).ToList();
            check(cloudRects.SelectMany((a, i) => cloudRects.Skip(i + 1).Select(b =>
                a.X >= b.Right || a.Right <= b.X || a.Y >= b.Bottom || a.Bottom <= b.Y)).All(x => x), "Word cloud layout has no overlap");
            Card_PointerEntered(originalCards[0], null!);
            check(originalCards[0].Background is SolidColorBrush { Color.A: 0 }, "Word cloud hover does not add a card background");
            UpdateEdgeHover(originalCards[0], new Point(1, 1));
            check(originalCards[0].BorderThickness == new Thickness(0) &&
                originalCards[0].Background is SolidColorBrush { Color.A: 0 }, "Word cloud edge hover adds no resize border");
            Canvas.SetLeft(originalCards[0], 700); Canvas.SetTop(originalCards[0], 500);
            Pack_Click(this, new RoutedEventArgs()); await Task.Delay(450);
            check(_isWordCloudMode, "Compact packing preserves word cloud mode");
            cloudRects = originalCards.Select(c => new Rect(Canvas.GetLeft(c), Canvas.GetTop(c), c.ActualWidth, c.ActualHeight)).ToList();
            check(cloudRects.SelectMany((a, i) => cloudRects.Skip(i + 1).Select(b =>
                a.X >= b.Right || a.Right <= b.X || a.Y >= b.Bottom || a.Bottom <= b.Y)).All(x => x), "Compact packing uses tight word cloud bounds");
            await _history.UndoAsync(); await Task.Delay(150);
            await Capture("word-cloud-paper");
            SetViewMode("tile"); await Settle();
            check(!_isWordCloudMode && originalCards.All(c =>
            {
                var state = canvasState[((ItemVM)c.DataContext).Id];
                return Math.Abs(c.Width - state.Width) < .01 && c.MinHeight == state.MinHeight &&
                    FindChildByName<TextBlock>(c, "ContentMeta")!.Visibility == Visibility.Visible;
            }), "Leaving word cloud restores the original canvas presentation");
            SetViewMode("list"); await Settle();
            check(CardCanvas.Children.Count == 0 && _listCards.Count == canvasState.Count &&
                _listCards.Select(c => ((ItemVM)c.DataContext).Id).Distinct().Count() == canvasState.Count,
                "Text list moves every item once");
            var dates = _listCards.Select(c => ((ItemVM)c.DataContext).CreatedAt).ToList();
            check(dates.SequenceEqual(dates.OrderByDescending(d => d)), "Text list is newest first");
            check(_listCards.All(c => c.CornerRadius == new CornerRadius(0) &&
                c.BorderThickness == new Thickness(0, 0, 0, 1) &&
                (c.Background == null || c.Background is SolidColorBrush { Color.A: 0 })),
                "Text rows have only a divider and no card shell");
            check(_listCards.All(c => FindChildByName<TextBlock>(c, "ContentText") is { FontSize: 15, LineHeight: 22 } &&
                FindChildByName<TextBlock>(c, "ContentMeta") is { Visibility: Visibility.Visible, Opacity: 1 }),
                "Text list resets low-zoom type size and metadata");
            var selectedRow = _listCards.First();
            SelectCard(selectedRow);
            check(ReferenceEquals(selectedRow.Background, ResourceBrush("SelectionBrush")), "List selection uses a row highlight");
            ClearSelection();
            check(selectedRow.BorderThickness == new Thickness(0, 0, 0, 1) &&
                selectedRow.Background is SolidColorBrush { Color.A: 0 }, "Clearing list selection restores the plain row");

            for (var cycle = 0; cycle < 2; cycle++)
            {
                SetViewMode("tile"); await Settle();
                var restored = CardCanvas.Children.OfType<Border>().ToList();
                check(_listCards.Count == 0 && restored.Count == canvasState.Count && restored.All(c =>
                {
                    var state = canvasState[((ItemVM)c.DataContext).Id];
                    return Math.Abs(c.Width - state.Width) < .01 && c.MinHeight == state.MinHeight && ReferenceEquals(c.Child, state.Child);
                }), "View round trip preserves canvas dimensions and content " + cycle);
                SetViewMode("list"); await Settle();
                check(_listCards.Count == canvasState.Count && _listCards.Select(c => ((ItemVM)c.DataContext).Id).Distinct().Count() == canvasState.Count,
                    "Repeated list mounting does not duplicate items " + cycle);
            }

            var scale = RootGrid.XamlRoot.RasterizationScale;
            foreach (var themeId in new[] { "paper", "twilight" })
            {
                SelectTheme(ThemeService.Themes.Single(t => t.Id == themeId));
                foreach (var width in new[] { 1240, 800 })
                {
                    AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(760 * scale)));
                    await Settle();
                    var name = $"text-list-{themeId}-{(width == 1240 ? "wide" : "narrow")}";
                    await Capture(name);
                    CheckRowsFit(name);
                }
            }

            var editItem = _listCards.Select(c => (ItemVM)c.DataContext).First(vm => vm.Content?.Contains('\n') == true);
            var originalText = editItem.Content!;
            ItemVM CurrentItem() => (ItemVM)_listCards.Single(c => ((ItemVM)c.DataContext).Id == editItem.Id).DataContext;
            async Task<string?> StoredText() => (await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == editItem.Id).Content;
            StartInlineEdit(editItem);
            check(_activeEditBox != null && NormalizeLines(_activeEditBox.Text) == NormalizeLines(originalText), "List editor opens the complete multiline text");
            _activeEditBox!.Text = "取消这次修改";
            EndInlineEdit(false); await Settle();
            check(await StoredText() == originalText && CurrentItem().Content == originalText, "Canceling a list edit preserves the original text");
            StartInlineEdit(CurrentItem());
            _activeEditBox!.Text = "  列表编辑保留空格\n\n日本語 🎵\n最后一行  ";
            var savedText = _activeEditBox.Text; // WinUI normalizes newlines to CR.
            EndInlineEdit(true); await _textSaveTask; await Settle();
            check(await StoredText() == savedText, "List edit saves full Unicode text and whitespace");
            CheckRowsFit("Saved text list");
            await _history.UndoAsync(); await Settle();
            check(_isListMode && await StoredText() == originalText, "Undo list edit preserves list mode and original text");
            await _history.RedoAsync(); await Settle();
            check(_isListMode && await StoredText() == savedText, "Redo list edit preserves list mode and full text");
            await _history.UndoAsync(); await Settle();
            StartInlineEdit(CurrentItem());
            _activeEditBox!.Text = "切换视图也保存全文\nA second line.";
            savedText = _activeEditBox.Text;
            SetViewMode("tile"); await _textSaveTask; await Settle();
            var restoredText = CardCanvas.Children.OfType<Border>().Single(c => ((ItemVM)c.DataContext).Id == editItem.Id);
            check(await StoredText() == savedText && FindChildByName<TextBlock>(restoredText, "ContentText")!.Text == savedText,
                "Leaving list mode saves and restores the full text on the canvas");
            await _history.UndoAsync(); await Settle();
        }
        finally
        {
            EndInlineEdit(save: true); await _textSaveTask;
            if (_isListMode) SetViewMode("tile");
            ChipText.IsChecked = false; ChipAllType.IsChecked = true;
            SelectTheme(previousTheme);
            AppWindow.Resize(previousSize);
            if (wasMaximized) presenter?.Maximize();
            await Settle();
        }
    }
}

internal sealed partial class VideoCardView
{
    internal Windows.Media.Playback.MediaPlayer? ReviewPlayer => _player;
    internal MediaPlayerElement ReviewMedia => _media;
    internal VideoViewerWindow? ReviewViewer => _viewer;
    internal ItemVM ReviewItem => _item;
    internal bool ReviewFailed => _failed;
}

internal sealed partial class VideoViewerWindow
{
    internal MediaPlayerElement ReviewMedia => _media;
}
