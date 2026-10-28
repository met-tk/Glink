using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Glink.Core;
using Windows.System;
using Windows.UI.Core;

namespace Glink;

public sealed partial class MainWindow
{
    private readonly UndoHistory _history = new();
    private bool _favoritesOnly;

    private void InitializeCommands()
    {
        _history.Changed += UpdateHistoryButtons;
        Closed += (_, _) => _history.Dispose();
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        ToolTipService.SetToolTip(UndoButton, $"撤回{_history.UndoName} ({ShortcutService.GestureOf("undo").Display})");
        ToolTipService.SetToolTip(RedoButton, $"重做{_history.RedoName} ({ShortcutService.GestureOf("redo").Display})");
    }

    private async Task ExecuteShortcutAsync(string id)
    {
        switch (id)
        {
            case "undo": await ApplyHistoryAsync(false); break;
            case "redo": await ApplyHistoryAsync(true); break;
            case "pack": Pack_Click(this, new RoutedEventArgs()); break;
            case "newText": AddText_Click(this, new RoutedEventArgs()); break;
            case "addFile": AddFile_Click(this, new RoutedEventArgs()); break;
            case "paste": await PasteFromClipboardAsync(); break;
            case "newCategory": NewCategoryButton_Click(this, new RoutedEventArgs()); break;
            case "viewAll": AllButton_Click(this, new RoutedEventArgs()); break;
            case "viewFavorites": FavoritesButton_Click(this, new RoutedEventArgs()); break;
            case "settings": SettingsButton_Click(this, new RoutedEventArgs()); break;
            case "zoomIn": ZoomCanvas(_requestedZoom * 1.1, CanvasViewportCenter()); break;
            case "zoomOut": ZoomCanvas(_requestedZoom / 1.1, CanvasViewportCenter()); break;
            case "zoomReset": ZoomCanvas(1, CanvasViewportCenter()); break;
        }
    }

    private static bool IsTypingOrDialog(DependencyObject? source)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or RichEditBox or PasswordBox or ContentDialog) return true;
        return false;
    }

    private async Task ApplyHistoryAsync(bool redo)
    {
        if (_history.IsBusy || _isImporting) return;
        EndInlineEdit(save: true);
        await _textSaveTask;
        var name = redo ? _history.RedoName : _history.UndoName;
        if (name == null) return;
        RootGrid.IsHitTestVisible = false;
        try
        {
            CancelCanvasDrag();
            if (redo) await _history.RedoAsync(); else await _history.UndoAsync();
            ImportStatus.Title = (redo ? "已重做：" : "已撤回：") + name;
            ImportStatus.Message = "";
            ImportStatus.Severity = InfoBarSeverity.Success;
            ImportStatus.IsClosable = true; ImportStatus.IsOpen = true;
        }
        catch (Exception error) { ShowImportError(redo ? "重做失败，请重试" : "撤回失败，请重试", error); }
        finally { RootGrid.IsHitTestVisible = true; }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e) => await ApplyHistoryAsync(false);
    private async void RedoButton_Click(object sender, RoutedEventArgs e) => await ApplyHistoryAsync(true);

    private void RecordDataChange(string name, Func<Task> undo, Func<Task> redo, IDisposable? retained = null)
    {
        async Task Apply(Func<Task> change)
        {
            ReleaseMediaForHistory();
            try { await change(); }
            finally
            {
                await RefreshCategoriesAsync();
                await RefreshItemsAsync();
            }
        }
        _history.Record(name, () => Apply(undo), () => Apply(redo), retained);
    }

    private void ReleaseMediaForHistory()
    {
        DisposeVideoCards();
        _mediaPlayer.Pause(); _mediaPlayer.Source = null;
        _progressTimer.Stop();
        if (_playingItem != null) _playingItem.IsPlaying = false;
        _playingItem = null;
    }

    private sealed class ArchivedItems(IEnumerable<Item> items) : IDisposable
    {
        private readonly List<Store.ItemArchive> _items = items.Select(i => new Store.ItemArchive(i)).ToList();
        public Task RemoveAsync() => ApplyAsync(false);
        public Task RestoreAsync() => ApplyAsync(true);
        private async Task ApplyAsync(bool restore)
        {
            var completed = new List<Store.ItemArchive>();
            try
            {
                foreach (var item in _items)
                {
                    if (restore) await item.RestoreAsync(); else await item.RemoveAsync();
                    completed.Add(item);
                }
            }
            catch
            {
                foreach (var item in completed.AsEnumerable().Reverse())
                    if (restore) await item.RemoveAsync(); else await item.RestoreAsync();
                throw;
            }
        }
        public void Dispose() { foreach (var item in _items) item.Dispose(); }
    }

    private void RecordAddedItems(IEnumerable<Item> items)
    {
        var retained = new ArchivedItems(items);
        RecordDataChange("添加灵感", retained.RemoveAsync, retained.RestoreAsync, retained);
    }

    private void FavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        _favoritesOnly = true; _selectedCategoryId = null; _selectedPresetId = null;
        _ = RefreshItemsAsync();
    }

    private Brush CardCategoryBrush(ItemVM item) =>
        _categories.FirstOrDefault(c => c.Id == item.CategoryId)?.AccentBrush ?? ResourceBrush("BorderBrush");

    private void RefreshCategoryColors()
    {
        foreach (var card in CardCanvas.Children.OfType<Border>())
            if (card.DataContext is ItemVM) card.BorderBrush = BorderBrushForCard(card, hovering: false);
        UpdateContentTitle();
    }

    private async Task ApplyCategoryColorAsync(Guid id, string hex)
    {
        await Store.SetCategoryColorAsync(id, hex);
        var category = _categories.FirstOrDefault(c => c.Id == id);
        if (category != null) category.AccentHex = hex;
        RefreshCategoryColors();
    }

    private async Task ChangeCategoryColorAsync(CategoryVM category, string hex)
    {
        var old = category.AccentHex;
        if (string.Equals(old, hex, StringComparison.OrdinalIgnoreCase)) return;
        await ApplyCategoryColorAsync(category.Id, hex);
        _history.Record("分类颜色", () => ApplyCategoryColorAsync(category.Id, old), () => ApplyCategoryColorAsync(category.Id, hex));
    }

    private sealed record CanvasArrangement(Guid? Category, ItemType? Type, string? Time, bool Favorites,
        Dictionary<Guid, (double X, double Y, int Z)> Positions);

    private CanvasArrangement CaptureArrangement() => new(_selectedCategoryId, _selectedType, _selectedTimeFilter, _favoritesOnly,
        CardCanvas.Children.OfType<Border>().ToDictionary(c => ((ItemVM)c.DataContext).Id,
            c => (Canvas.GetLeft(c), Canvas.GetTop(c), Canvas.GetZIndex(c))));

    private async Task RestoreArrangementAsync(CanvasArrangement arrangement)
    {
        if (_selectedCategoryId != arrangement.Category || _selectedType != arrangement.Type ||
            _selectedTimeFilter != arrangement.Time || _favoritesOnly != arrangement.Favorites)
        {
            _selectedCategoryId = arrangement.Category; _selectedType = arrangement.Type;
            _selectedTimeFilter = arrangement.Time; _favoritesOnly = arrangement.Favorites;
            ChipAllType.IsChecked = _selectedType == null; ChipText.IsChecked = _selectedType == ItemType.Text;
            ChipImage.IsChecked = _selectedType == ItemType.Image; ChipAudio.IsChecked = _selectedType == ItemType.Audio;
            ChipVideo.IsChecked = _selectedType == ItemType.Video;
            ChipAllTime.IsChecked = _selectedTimeFilter == null; ChipToday.IsChecked = _selectedTimeFilter == "today";
            ChipWeek.IsChecked = _selectedTimeFilter == "week"; ChipEarlier.IsChecked = _selectedTimeFilter == "earlier";
            await RefreshItemsAsync();
        }
        _layoutAnimationTimer?.Stop(); _activeTransitions.Clear();
        foreach (var card in CardCanvas.Children.OfType<Border>())
            if (arrangement.Positions.TryGetValue(((ItemVM)card.DataContext).Id, out var p))
            { Canvas.SetLeft(card, p.X); Canvas.SetTop(card, p.Y); Canvas.SetZIndex(card, p.Z); }
        _maxZIndex = CardCanvas.Children.OfType<Border>().Select(Canvas.GetZIndex).DefaultIfEmpty(0).Max();
        UpdateCanvasExtent();
    }

    private async Task RestoreMovedItemAsync(Guid id, double? x, double? y, int z, CanvasArrangement arrangement)
    {
        await Store.SetItemPositionAsync(id, x, y, z);
        await RestoreArrangementAsync(arrangement);
        var item = CardCanvas.Children.OfType<Border>().Select(c => (ItemVM)c.DataContext).FirstOrDefault(i => i.Id == id);
        if (item != null) { item.PosX = x; item.PosY = y; item.ZIndex = z; }
    }

    private void RecordArrangement(CanvasArrangement before, CanvasArrangement after)
    {
        if (before.Positions.Count == after.Positions.Count && before.Positions.All(p => after.Positions.GetValueOrDefault(p.Key) == p.Value)) return;
        _history.Record("紧凑平铺", () => RestoreArrangementAsync(before), () => RestoreArrangementAsync(after));
    }
}
