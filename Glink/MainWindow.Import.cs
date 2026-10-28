using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Glink.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Glink;

public sealed partial class MainWindow
{
    private bool IsInternalDrag(DragEventArgs e) =>
        _draggingCard != null || _categoryDragActive || e.DataView.Contains(InternalDragFormat);

    private Guid? GetDropCategory(DragEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node == AllButton) return null;
            if (node is FrameworkElement { DataContext: CategoryVM category }) return category.Id;
        }
        return _selectedCategoryId;
    }

    private void ContentArea_DragOver(object sender, DragEventArgs e)
    {
        // The category handler owns internal moves, including its AcceptedOperation.
        if (IsInternalDrag(e)) return;
        e.Handled = true;
        _dropLeaveTimer.Stop();
        if (_isImporting || !ExternalDropImporter.CanRead(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        var categoryId = GetDropCategory(e);
        var category = _categories.FirstOrDefault(c => c.Id == categoryId);
        DropHint.Text = category == null ? "释放以添加灵感" : $"释放以添加到「{category.Name}」";
        e.DragUIOverride.Caption = DropHint.Text;
        e.DragUIOverride.IsCaptionVisible = true;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void ContentArea_DragLeave(object sender, DragEventArgs e)
    {
        if (IsInternalDrag(e)) return;
        // DragLeave also bubbles when crossing children. A following DragOver cancels this hide.
        // Cancellation (Escape) and leaving the window have no subsequent DragOver.
        _dropLeaveTimer.Start();
    }

    private async void ContentArea_Drop(object sender, DragEventArgs e)
    {
        if (IsInternalDrag(e)) return;
        e.Handled = true; // Before the first await: this routed drop is consumed exactly once.
        _dropLeaveTimer.Stop();
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_isImporting || !ExternalDropImporter.CanRead(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var categoryId = GetDropCategory(e); // Freeze destination before any asynchronous work.
        var data = e.DataView;
        var deferral = e.GetDeferral();
        e.AcceptedOperation = DataPackageOperation.Copy;
        _isImporting = true;
        AddInspirationBtn.IsEnabled = false;
        try
        {
            ShowImportProgress("正在读取拖入内容…");
            await _storeInitialization;
            var result = await _dropImporter.ImportAsync(data, categoryId, ShowImportProgress);
            e.AcceptedOperation = result.Items.Count > 0 ? DataPackageOperation.Copy : DataPackageOperation.None;
            await FinishImportAsync(result, categoryId);
        }
        catch (Exception error)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ShowImportError("拖入内容添加失败", error);
        }
        finally
        {
            _isImporting = false;
            AddInspirationBtn.IsEnabled = true;
            deferral.Complete();
        }
    }

    private void ShowImportProgress(string fileName)
    {
        ImportStatus.Title = "正在添加灵感";
        ImportStatus.Message = fileName;
        ImportStatus.Severity = InfoBarSeverity.Informational;
        ImportStatus.IsClosable = false;
        ImportStatus.IsOpen = true;
    }

    private async Task FinishImportAsync(ImportResult result, Guid? categoryId, string? emptyHint = null)
    {
        var filtersReset = false;
        if (result.Items.Count > 0)
        {
            RecordAddedItems(result.Items);
            _favoritesOnly = false;
            if (_selectedCategoryId == categoryId)
            {
                // Newly imported objects must not disappear behind a type/"earlier" filter.
                if (_selectedType.HasValue && result.Items.Any(item => item.Type != _selectedType))
                {
                    _selectedType = null;
                    foreach (var chip in new[] { ChipAllType, ChipText, ChipImage, ChipAudio, ChipVideo })
                        chip.IsChecked = chip == ChipAllType;
                    filtersReset = true;
                }
                if (_selectedTimeFilter == "earlier")
                {
                    _selectedTimeFilter = null;
                    foreach (var chip in new[] { ChipAllTime, ChipToday, ChipWeek, ChipEarlier })
                        chip.IsChecked = chip == ChipAllTime;
                    filtersReset = true;
                }

            }
            await RefreshCategoriesAsync();
            await RefreshItemsAsync();
        }

        var details = new List<string>();
        var targetCategory = _categories.FirstOrDefault(category => category.Id == categoryId);
        if (result.Items.Count > 0 && targetCategory != null)
            details.Add($"已保存到「{targetCategory.Name}」。");
        if (filtersReset) details.Add("已调整筛选条件以显示新灵感。");
        if (result.Skipped.Count > 0)
            details.Add($"跳过 {result.Skipped.Count} 个不支持的文件或文件夹：{string.Join("、", result.Skipped.Take(3))}");
        foreach (var failure in result.Failures.Take(3))
        {
            System.Diagnostics.Debug.WriteLine(failure.Error);
            details.Add($"{failure.Name}：{DescribeImportError(failure.Error)}");
        }
        if (result.Failures.Count > 3) details.Add($"另有 {result.Failures.Count - 3} 个文件添加失败。");
        if (result.Items.Count == 0 && details.Count == 0)
            details.Add(emptyHint ?? "请拖入文本、图片、音频或视频文件，也可以直接拖入选中的文字。");

        ImportStatus.Title = result.Items.Count > 0 ? $"已添加 {result.Items.Count} 个灵感" : "未添加灵感";
        ImportStatus.Message = string.Join("\n", details);
        ImportStatus.Severity = result.Failures.Count > 0 || result.Skipped.Count > 0 || result.Items.Count == 0
            ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ImportStatus.IsClosable = true;
        ImportStatus.IsOpen = true;
    }

    private static string DescribeImportError(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "文件已移动或不存在，请重新选择",
        UnauthorizedAccessException => "无法读取文件，请检查访问权限",
        InvalidDataException or NotSupportedException => error.Message,
        System.Text.DecoderFallbackException => "无法识别文本编码，请另存为 UTF-8 后重试",
        IOException => "文件无法读写，请检查文件是否被占用及磁盘剩余空间",
        _ => "无法读取或保存，请重新拖入或使用“添加灵感”选择文件"
    };

    // ====================================================================
    //  Paste import: Ctrl+V reads the clipboard and drops the new cards
    //  at the current mouse cursor position on the canvas.
    // ====================================================================

    private async Task PasteFromClipboardAsync()
    {
        if (_isImporting) return;
        DataPackageView? data;
        try { data = Clipboard.GetContent(); }
        catch (Exception error)
        {
            ShowImportError("无法读取剪贴板", error);
            return;
        }
        if (data == null || !ExternalDropImporter.CanRead(data))
        {
            ImportStatus.Title = "剪贴板中没有可导入的内容";
            ImportStatus.Message = "复制文本、图片、音频或视频文件后，再按粘贴。";
            ImportStatus.Severity = InfoBarSeverity.Informational;
            ImportStatus.IsClosable = true;
            ImportStatus.IsOpen = true;
            return;
        }

        var categoryId = _selectedCategoryId; // Freeze destination before any asynchronous work.
        var target = GetPasteTargetPoint();
        _isImporting = true;
        AddInspirationBtn.IsEnabled = false;
        try
        {
            ShowImportProgress("正在读取粘贴内容…");
            await _storeInitialization;
            var result = await _dropImporter.ImportAsync(data, categoryId, ShowImportProgress);
            await FinishImportAsync(result, categoryId,
                "剪贴板中没有文本、图片、音频或视频文件。");
            if (result.Items.Count > 0 && target is { } point)
                await PlaceImportedItemsAsync(result.Items, point);
        }
        catch (Exception error)
        {
            ShowImportError("粘贴内容添加失败", error);
        }
        finally
        {
            _isImporting = false;
            AddInspirationBtn.IsEnabled = true;
        }
    }

    /// <summary>鼠标光标在画布内容坐标系中的落点；光标不在画布内或列表模式下回退自动平铺。</summary>
    private Point? GetPasteTargetPoint()
    {
        if (_isListMode || _lastMouseCanvasPoint is not { } viewportPoint) return null;
        try
        {
            if (viewportPoint.X < 0 || viewportPoint.Y < 0 ||
                viewportPoint.X > CanvasScroll.ActualWidth || viewportPoint.Y > CanvasScroll.ActualHeight)
                return null;
            var content = CanvasScroll.TransformToVisual(CardCanvas).TransformPoint(viewportPoint);
            return new Point(Math.Max(0, content.X), Math.Max(0, content.Y));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把刚粘贴生成的卡片按光标落点依次摆放（多内容级联 24px），并持久化坐标与层级。</summary>
    private async Task PlaceImportedItemsAsync(IReadOnlyList<Item> items, Point point)
    {
        var cards = CardCanvas.Children.OfType<Border>()
            .Where(card => card.DataContext is ItemVM)
            .ToDictionary(card => ((ItemVM)card.DataContext).Id);
        var cascade = 0;
        foreach (var item in items)
        {
            var x = Math.Max(0, point.X + cascade);
            var y = Math.Max(0, point.Y + cascade);
            var z = ++_maxZIndex;
            await Store.SetItemPositionAsync(item.Id, x, y, z);
            if (cards.TryGetValue(item.Id, out var card))
            {
                Canvas.SetLeft(card, x);
                Canvas.SetTop(card, y);
                Canvas.SetZIndex(card, z);
                if (card.DataContext is ItemVM vm) { vm.PosX = x; vm.PosY = y; vm.ZIndex = z; }
            }
            cascade += 24;
        }
        UpdateCanvasExtent();
    }

    private void ShowImportError(string title, Exception error)
    {
        System.Diagnostics.Debug.WriteLine(error);
        ImportStatus.Title = title;
        ImportStatus.Message = DescribeImportError(error);
        ImportStatus.Severity = InfoBarSeverity.Error;
        ImportStatus.IsClosable = true;
        ImportStatus.IsOpen = true;
    }
}
