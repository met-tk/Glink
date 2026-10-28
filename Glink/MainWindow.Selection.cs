using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Glink.Core;
using Windows.Foundation;
using Windows.System;

namespace Glink;

// ====================================================================
//  画布多选：Shift + 左键拖拽框选；Ctrl + 左键点选增减；
//  选中多张后右键出批量菜单（删除 / 归类 / 收藏 / 取消选择）。
//  选择是视图级状态：刷新（切分类/筛选）即清空，不落库。
// ====================================================================

public sealed partial class MainWindow
{
    private readonly HashSet<Guid> _selectedItemIds = new();
    private readonly List<Border> _selectedCards = new();
    private bool HasSelection => _selectedItemIds.Count > 0;

    // 框选拖拽状态（内容坐标系 = CardCanvas 坐标，随缩放自然一致）。
    private bool _marqueeActive;
    private Point _marqueeStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _marqueeOverlay;

    // 同一次按下是否落在卡片上：卡片处理器先于 CanvasHost 收到事件并打标，
    // CanvasHost 侧据此区分"空白按下清选"与"卡片按下"。
    private Border? _cardPressMarker;

    private void InitializeSelection()
    {
        // handledEventsToo：视频表面等子控件吞掉的按下事件仍能看到，
        // Shift+左键在任何位置都能起手框选。
        CanvasHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(CanvasHost_SelectionPointerPressed), true);
        CanvasHost.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(CanvasHost_SelectionPointerMoved), true);
        CanvasHost.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(CanvasHost_SelectionPointerReleased), true);
    }

    // ------------------------------------------------------------------
    //  选择状态与视觉
    // ------------------------------------------------------------------

    private bool IsCardSelected(Border card) =>
        card.DataContext is ItemVM vm && _selectedItemIds.Contains(vm.Id);

    private void ToggleCardSelection(Border card)
    {
        if (IsCardSelected(card)) DeselectCard(card);
        else SelectCard(card);
    }

    private void SelectCard(Border card)
    {
        if (card.DataContext is not ItemVM vm || !_selectedItemIds.Add(vm.Id)) return;
        _selectedCards.Add(card);
        ApplyCardSelectionVisual(card, selected: true);
    }

    private void DeselectCard(Border card)
    {
        if (card.DataContext is not ItemVM vm) return;
        if (!_selectedItemIds.Remove(vm.Id)) return;
        _selectedCards.RemoveAll(c => c.DataContext is ItemVM v && v.Id == vm.Id);
        ApplyCardSelectionVisual(card, selected: false);
    }

    /// <summary>清空多选并还原卡片边框（选中的卡片边框被临时替换为强调色）。</summary>
    private void ClearSelection()
    {
        foreach (var card in _selectedCards) ApplyCardSelectionVisual(card, selected: false);
        _selectedCards.Clear();
        _selectedItemIds.Clear();
    }

    private void ApplyCardSelectionVisual(Border card, bool selected)
    {
        if (card.DataContext is not ItemVM vm) return;
        if (IsTextListRow(card))
        {
            ApplyTextListVisual(card, selected);
            return;
        }
        if (IsWordCloudTextCard(card))
        {
            card.BorderThickness = new Thickness(0);
            return;
        }
        if (selected)
        {
            card.BorderBrush = ResourceBrush("AccentBrush");
            card.BorderThickness = new Thickness(2);
        }
        else
        {
            card.BorderBrush = vm.CategoryId.HasValue ? CardCategoryBrush(vm) : ResourceBrush("BorderBrush");
            card.BorderThickness = new Thickness(vm.CategoryId.HasValue ? 2 : 1);
        }
    }

    /// <summary>悬停/恢复时的边框取色：选中卡片恒为强调色，悬停与分类换色都不得覆盖选中视觉。</summary>
    private Brush BorderBrushForCard(Border card, bool hovering)
    {
        if (IsCardSelected(card)) return ResourceBrush("AccentBrush");
        if (card.DataContext is ItemVM item && item.CategoryId.HasValue) return CardCategoryBrush(item);
        return ResourceBrush(hovering ? "BorderHoverBrush" : "BorderBrush");
    }

    // ------------------------------------------------------------------
    //  Shift + 左键框选
    // ------------------------------------------------------------------

    private void CanvasHost_SelectionPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isListMode) { _cardPressMarker = null; return; }
        var pressed = e.GetCurrentPoint(CanvasHost).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        if (!pressed) { _cardPressMarker = null; return; }

        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            StartMarquee(e);
            e.Handled = true;
            _cardPressMarker = null;
            return;
        }
        // 空白处普通左键（无任何修饰键）：清掉旧多选（卡片上的按下已在
        // CanvasCard_PointerPressed 标记；Ctrl+点空白保留选择，与资源管理器一致）。
        if (_cardPressMarker == null && HasSelection && e.KeyModifiers == VirtualKeyModifiers.None)
            ClearSelection();
        _cardPressMarker = null;
    }

    private void StartMarquee(PointerRoutedEventArgs e)
    {
        _marqueeActive = true;
        _marqueeStart = e.GetCurrentPoint(CardCanvas).Position;
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
        _marqueeOverlay = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            IsHitTestVisible = false,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, accent.R, accent.G, accent.B)),
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(140, accent.R, accent.G, accent.B)),
            StrokeThickness = 1,
            RadiusX = 2,
            RadiusY = 2
        };
        Canvas.SetZIndex(_marqueeOverlay, 99999);
        UpdateMarqueeRect(_marqueeStart);
        CardCanvas.Children.Add(_marqueeOverlay);
        try { CanvasHost.CapturePointer(e.Pointer); }
        catch { /* 捕获失败也能工作，只是离开宿主区域后收不到 move */ }
    }

    private void CanvasHost_SelectionPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        UpdateMarqueeRect(e.GetCurrentPoint(CardCanvas).Position);
        e.Handled = true;
    }

    private void CanvasHost_SelectionPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _cardPressMarker = null;
        if (!_marqueeActive) return;
        _marqueeActive = false;
        try { CanvasHost.ReleasePointerCapture(e.Pointer); } catch { /* Silent */ }
        var rect = MarqueeBounds(e.GetCurrentPoint(CardCanvas).Position);
        if (_marqueeOverlay != null) CardCanvas.Children.Remove(_marqueeOverlay);
        _marqueeOverlay = null;
        // 过小的拖动（误触）不改变选择。
        if (rect.Width >= 4 || rect.Height >= 4)
            CompleteMarquee(rect, additive: e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
        e.Handled = true;
    }

    private void UpdateMarqueeRect(Point current)
    {
        if (_marqueeOverlay == null) return;
        var rect = MarqueeBounds(current);
        Canvas.SetLeft(_marqueeOverlay, rect.X);
        Canvas.SetTop(_marqueeOverlay, rect.Y);
        _marqueeOverlay.Width = rect.Width;
        _marqueeOverlay.Height = rect.Height;
    }

    private Rect MarqueeBounds(Point current)
    {
        var x = Math.Min(_marqueeStart.X, current.X);
        var y = Math.Min(_marqueeStart.Y, current.Y);
        return new Rect(x, y, Math.Abs(current.X - _marqueeStart.X), Math.Abs(current.Y - _marqueeStart.Y));
    }

    /// <summary>框选完成：与选框相交的卡片入选；Ctrl 按住时为增量选择，否则替换。</summary>
    private void CompleteMarquee(Rect rect, bool additive)
    {
        if (!additive) ClearSelection();
        foreach (var card in CardCanvas.Children.OfType<Border>().ToList())
        {
            if (card.DataContext is not ItemVM vm) continue;
            var bounds = new Rect(Canvas.GetLeft(card), Canvas.GetTop(card),
                BinPackingLayoutEngine.GetCardWidth(card), BinPackingLayoutEngine.GetCardHeight(card));
            if (bounds.X < rect.Right && bounds.Right > rect.X && bounds.Y < rect.Bottom && bounds.Bottom > rect.Y)
                SelectCard(card);
        }
    }

    // ------------------------------------------------------------------
    //  多选右键菜单与批量操作
    // ------------------------------------------------------------------

    /// <summary>右键落在选中卡片上且多于一张 → 批量菜单；否则走单卡菜单（并清掉旧多选）。</summary>
    private bool TryShowMultiSelectMenu(FrameworkElement target, Point position)
    {
        if (_selectedItemIds.Count < 2 || target is not Border card || !IsCardSelected(card)) return false;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = $"已选 {_selectedItemIds.Count} 条灵感", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());

        var categorize = new MenuFlyoutSubItem { Text = "归类到…" };
        foreach (var category in _categories)
        {
            var target1 = category;
            var item = new MenuFlyoutItem { Text = category.Name };
            item.Click += (_, _) => _ = RunBatchAsync(() => BatchCategorizeAsync(target1.Id, target1.Name));
            categorize.Items.Add(item);
        }
        var uncategorized = new MenuFlyoutItem { Text = "移出分类" };
        uncategorized.Click += (_, _) => _ = RunBatchAsync(() => BatchCategorizeAsync(null, "未分类"));
        categorize.Items.Add(uncategorized);
        menu.Items.Add(categorize);

        var allFavorite = _selectedCards.All(c => c.DataContext is ItemVM vm && vm.IsFavorite);
        var favorite = new MenuFlyoutItem { Text = allFavorite ? "取消收藏" : "收藏", Icon = new FontIcon { Glyph = allFavorite ? "\uE735" : "\uE734" } };
        favorite.Click += (_, _) => _ = RunBatchAsync(BatchFavoriteAsync);
        menu.Items.Add(favorite);

        var export = new MenuFlyoutItem { Text = "导出所选…", Icon = new FontIcon { Glyph = "\uE898" } };
        export.Click += (_, _) => _ = RunBatchAsync(BatchExportSelectedAsync);
        menu.Items.Add(export);

        var remove = new MenuFlyoutItem { Text = $"删除 {_selectedItemIds.Count} 条", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += (_, _) => _ = RunBatchAsync(BatchDeleteSelectedAsync);
        menu.Items.Add(remove);

        menu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "取消选择" };
        clear.Click += (_, _) => ClearSelection();
        menu.Items.Add(clear);

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
        return true;
    }

    private async Task RunBatchAsync(Func<Task> batch)
    {
        try { await batch(); }
        catch (Exception error) { ShowImportError("批量操作未完成，请重试", error); }
    }

    private async Task BatchDeleteSelectedAsync()
    {
        var count = _selectedItemIds.Count;
        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "删除灵感",
            Content = new TextBlock { Text = $"确定删除选中的 {count} 条灵感？删除后可通过撤回恢复。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Background = (Brush)Application.Current.Resources["CardBrush"],
            Foreground = (Brush)Application.Current.Resources["TextBrush"]
        };
        if (await ThemeService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        EndInlineEdit(save: true); await _textSaveTask;
        var snapshots = (await Store.GetItemsAsync(null, null, null, false))
            .Where(i => _selectedItemIds.Contains(i.Id)).ToList();
        if (snapshots.Count == 0) return;
        ReleaseMediaForHistory();
        var retained = new ArchivedItems(snapshots);
        await retained.RemoveAsync();
        RecordDataChange($"删除 {snapshots.Count} 条灵感", retained.RestoreAsync, retained.RemoveAsync, retained);
        ClearSelection();
        await RefreshCategoriesAsync();
        await RefreshItemsAsync();
    }

    private async Task BatchCategorizeAsync(Guid? categoryId, string label)
    {
        EndInlineEdit(save: true); await _textSaveTask;
        var olds = (await Store.GetItemsAsync(null, null, null, false))
            .Where(i => _selectedItemIds.Contains(i.Id))
            .ToDictionary(i => i.Id, i => i.CategoryId);
        if (olds.Count == 0) return;
        foreach (var id in olds.Keys) await Store.MoveItemAsync(id, categoryId);
        RecordDataChange($"归类到「{label}」",
            async () => { foreach (var (id, old) in olds) await Store.MoveItemAsync(id, old); },
            async () => { foreach (var id in olds.Keys) await Store.MoveItemAsync(id, categoryId); });
        ClearSelection();
        await RefreshCategoriesAsync();
        await RefreshItemsAsync();
    }

    private async Task BatchFavoriteAsync()
    {
        EndInlineEdit(save: true); await _textSaveTask;
        var ids = _selectedItemIds.ToList();
        if (ids.Count == 0) return;
        foreach (var id in ids) await Store.ToggleFavoriteAsync(id);
        // 收藏是开关量：撤销/重做都是再翻转一遍。
        RecordDataChange($"收藏 {ids.Count} 条灵感",
            async () => { foreach (var id in ids) await Store.ToggleFavoriteAsync(id); },
            async () => { foreach (var id in ids) await Store.ToggleFavoriteAsync(id); });
        ClearSelection();
        await RefreshCategoriesAsync();
        await RefreshItemsAsync();
    }
}
