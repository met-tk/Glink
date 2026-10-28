using Glink.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glink;

public sealed partial class MainWindow
{
    private async Task RunExportReviewAsync()
    {
        var log = new List<string>();
        var failures = 0;
        void Check(bool value, string name)
        {
            if (!value) failures++;
            log.Add((value ? "PASS " : "FAIL ") + name);
        }
        IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T current) yield return current;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        async Task<ContentDialog> OpenDialog()
        {
            for (var n = 0; n < 60; n++)
            {
                var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).SelectMany(p => Descendants<ContentDialog>(p.Child)).FirstOrDefault();
                if (dialog != null) { await Task.Delay(180); return dialog; }
                await Task.Delay(50);
            }
            throw new InvalidOperationException("Dialog did not open");
        }
        async Task Capture(FrameworkElement element, string name)
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[checked((int)pixels.Length)];
            reader.ReadBytes(bytes);
            var directory = await StorageFolder.GetFolderFromPathAsync(Store.DataDirectory);
            var file = await directory.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
        }
        static string Color(Brush? brush) => brush is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "none";
        try
        {
            await _storeInitialization;
            await Task.Delay(700);
            var items = await Store.GetItemsAsync(null, null, null, false);
            var categories = await Store.GetCategoriesAsync();
            var folders = new[] { new ExportFolder(Guid.NewGuid(), "周末灵感", categories.Select(c => c.Id).ToArray()) };
            var before = items.Count;
            foreach (var theme in ThemeService.Themes)
            {
                SelectTheme(theme);
                await Task.Delay(220); // SelectTheme applies on the next dispatcher turn.
                var deleting = DeleteCategoryAsync(_categories[0]);
                var dialog = await OpenDialog();
                var background = FindChildByName<Border>(dialog, "BackgroundElement")!;
                var close = FindChildByName<Button>(dialog, "CloseButton")!;
                var presenter = FindChildByName<ContentPresenter>(close, "ContentPresenter")!;
                Check(dialog.ActualTheme == (theme.IsLight ? ElementTheme.Light : ElementTheme.Dark), theme.Id + " deletion uses selected theme");
                Check(Color(background.Background) == theme.Card, theme.Id + " deletion surface matches palette");
                Check(Color(presenter.Background) == theme.Accent && Color(presenter.Foreground) == theme.OnAccent, theme.Id + " safe default button uses theme accent and contrasting text");
                if (theme.Id is "blossom" or "twilight" or "blank") await Capture(background, "delete-" + theme.Id);
                dialog.Hide();
                await deleting;
                await Task.Delay(200);

                var choosing = ChooseExportAsync(items, categories, folders);
                dialog = await OpenDialog();
                background = FindChildByName<Border>(dialog, "BackgroundElement")!;
                Check(Color(background.Background) == theme.Card, theme.Id + " export surface matches palette");
                foreach (var scope in Enum.GetValues<ExportScope>())
                {
                    var button = Descendants<ToggleButton>(dialog).First(b => b.Tag is ExportScope s && s == scope);
                    if (button.IsChecked != true)
                        ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();
                    await Task.Delay(160);
                    Check(button.IsChecked == true && dialog.IsPrimaryButtonEnabled, theme.Id + " " + scope + " selects nonempty export scope");
                    var boxes = Descendants<CheckBox>(dialog).ToArray();
                    if (boxes.Length > 0)
                    {
                        foreach (var box in boxes) box.IsChecked = false;
                        Check(!dialog.IsPrimaryButtonEnabled, theme.Id + " " + scope + " prevents empty selection");
                        foreach (var box in boxes) box.IsChecked = true;
                        var checkFill = Descendants<Border>(boxes[0]).FirstOrDefault(b => b.Name == "CheckBackground");
                        if (checkFill != null) Check(Color(checkFill.Background) == theme.Accent, theme.Id + " checkbox matches accent");
                    }
                    if (theme.Id is "blossom" or "twilight") await Capture(background, "export-" + theme.Id + "-" + scope);
                }
                dialog.Hide();
                Check(await choosing == null, theme.Id + " cancel produces no export request");
                await Task.Delay(200);
            }
            Check((await Store.GetItemsAsync(null, null, null, false)).Count == before && (await Store.GetCategoriesAsync()).Count == categories.Count,
                "Cancelling deletion/export dialogs preserves library");
            var empty = ChooseExportAsync([], [], []);
            var emptyDialog = await OpenDialog();
            Check(!emptyDialog.IsPrimaryButtonEnabled, "Empty library cannot export");
            emptyDialog.Hide();
            await empty;
        }
        catch (Exception error) { Check(false, error.ToString()); }
        finally
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot))
                foreach (var dialog in Descendants<ContentDialog>(popup.Child)) dialog.Hide();
            log.Add($"Export UI review: {log.Count - failures}/{log.Count} passed; {failures} failed.");
            await File.WriteAllLinesAsync(Path.Combine(Store.DataDirectory, "export-ui-review.txt"), log);
            Close();
        }
    }
}
