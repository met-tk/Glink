using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Glink.Core;
using Windows.Foundation;

namespace Glink;

public sealed partial class MainWindow
{
    private Border? _dragPreview;
    private Border? _dragLabel;
    private Point _dragOrigin;
    private Point _dragOffset;
    private Point _dragPointerRoot;
    private int _dragGeneration;
    // 整组拖拽：按住选中组内任一卡片时，其余选中卡的原位 + 各自的悬浮预览。
    private List<(Border Card, double X, double Y)>? _pendingGroupDrag;
    private List<(Border Preview, double OffsetX, double OffsetY)>? _groupDragPreviews;
    // 视频卡点按/拖动分流：普通左键是否按下在画面内部（松开未拖动 → 点按播放）。
    private bool _videoSurfacePress;

    private void CanvasCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || e.Handled || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        // resize 进行中：忽略其它指针的按下（多点触控防串扰）。
        if (_resize != null) return;
        // Ctrl + 左键：点选增减多选，不进入拖拽（列表模式同样可用批量菜单）。
        // 标记"按下起于卡片"：画布层（handledEventsToo 挂接、能看到已处理事件）
        // 依赖它区分卡片按下与空白按下，否则刚切换的选中会被误清。
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            _cardPressMarker = card;
            ToggleCardSelection(card);
            e.Handled = true;
            return;
        }
        // Shift + 左键保留给画布框选（CanvasHost 侧起手），卡片自身不做拖拽。
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) return;
        // 视频卡点按/拖动分流：普通左键按在画面内部 → 拖动阈值内松开算点按播放；
        // 超过阈值即拖动卡片。把手环/页脚不受影响。每次普通左键按下都覆写，防残留。
        _videoSurfacePress = card.Child is VideoCardView videoCard
                             && videoCard.IsInsideSurfaceInner(e.GetCurrentPoint(card).Position);
        // 多选组拖动优先于边缘调整：选中组后从任一卡片按下都表示移动整组。
        // 这样不会因按在边缘而误进入单卡 resize 会话。
        var groupDrag = IsCardSelected(card) && _selectedItemIds.Count >= 2;
        if (!groupDrag && TryStartResize(card, e)) return;
        // 普通左键落在未选中的卡片上：先清掉旧多选。
        if (HasSelection && !IsCardSelected(card)) ClearSelection();
        _cardPressMarker = card;
        // 列表模式不做画布拖拽；右键菜单与卡片自身交互不受影响。
        if (_isListMode) return;
        if (_editingItem != null && ReferenceEquals(card.DataContext, _editingItem)) return;
        if (!IsCardMoveHandle(card, e.GetCurrentPoint(card).Position)) return;
        _pendingDragCard = card;
        _pendingDragStart = e.GetCurrentPoint(RootGrid).Position;
        _pendingDragPointerId = e.Pointer.PointerId;
        _pendingDragMoved = false;
        _dragOrigin = new Point(Canvas.GetLeft(card), Canvas.GetTop(card));
        // 整组拖拽：按住选中组内任一卡片（普通左键），整组一起移动。
        _pendingGroupDrag = IsCardSelected(card) && _selectedItemIds.Count >= 2
            ? _selectedCards.Where(c => c != card).Select(c => (c, Canvas.GetLeft(c), Canvas.GetTop(c))).ToList()
            : null;
        var topLeft = card.TransformToVisual(RootGrid).TransformPoint(new Point());
        _dragOffset = new Point(_pendingDragStart.X - topLeft.X, _pendingDragStart.Y - topLeft.Y);
        // Capture on press so a fast move cannot leave the card before the threshold event.
        // Waveform/editor presses are already handled by their own controls.
        if (!card.CapturePointer(e.Pointer)) CancelCanvasDrag();
    }

    private static bool IsCardMoveHandle(Border card, Point local)
    {
        if (card.DataContext is not ItemVM { Type: ItemType.Audio }) return true;
        // Only the outer padding is a move handle; the waveform and footer keep their own gestures.
        const double edge = 20;
        return local.X <= edge || local.X >= card.ActualWidth - edge ||
               local.Y <= edge || local.Y >= card.ActualHeight - edge;
    }

    private void CanvasCard_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        // resize 进行中：尺寸跟随起手指针（其它指针的移动不干扰），不再走拖动逻辑。
        if (_resize is { } session)
        {
            if (ReferenceEquals(card, session.Card)) UpdateResize(e);
            return;
        }
        // 拖动期间不再执行边缘悬停命中，避免组拖动时光标切换成 resize 光标。
        if (_pendingDragCard == null && _draggingCard == null)
            UpdateEdgeHover(card, e.GetCurrentPoint(card).Position);
        else if (_resize == null && _activeCursor != null)
            SetWindowCursor(null);
        if (_pendingDragCard is not Border draggingCard || e.Pointer.PointerId != _pendingDragPointerId || !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
        var pos = e.GetCurrentPoint(RootGrid).Position;
        if (!_pendingDragMoved)
        {
            if (Math.Abs(pos.X - _pendingDragStart.X) < 6 && Math.Abs(pos.Y - _pendingDragStart.Y) < 6) return;
            _layoutAnimationTimer?.Stop();
            _activeTransitions.Clear();
            _pendingDragMoved = true;
            _draggingCard = draggingCard;
            _dragPointerRoot = pos;
            _ = _pendingGroupDrag is { Count: > 0 }
                ? CreateGroupDragPreviewsAsync(draggingCard, ++_dragGeneration)
                : CreateDragPreviewAsync(draggingCard, ++_dragGeneration);
        }
        _dragPointerRoot = pos;
        UpdateCanvasDragHover(pos);
        PositionDragPreview();
        e.Handled = true;
    }

    /// <summary>为整组选中卡生成悬浮预览：主卡沿用单卡预览（含分类提示标签），其余成员各一份快照。</summary>
    private async Task CreateGroupDragPreviewsAsync(Border card, int generation)
    {
        _groupDragPreviews = new List<(Border, double, double)>();
        if (_pendingGroupDrag != null)
        {
            foreach (var (member, memberX, memberY) in _pendingGroupDrag)
            {
                var preview = await SnapshotCardPreviewAsync(member, generation);
                if (preview == null) continue;
                var offsetX = (memberX - _dragOrigin.X) * CanvasScroll.ZoomFactor;
                var offsetY = (memberY - _dragOrigin.Y) * CanvasScroll.ZoomFactor;
                Canvas.SetLeft(preview, _dragPointerRoot.X - _dragOffset.X + offsetX);
                Canvas.SetTop(preview, _dragPointerRoot.Y - _dragOffset.Y + offsetY);
                preview.Visibility = Visibility.Collapsed;
                DragLayer.Children.Add(preview);
                member.Opacity = 0.22;
                _groupDragPreviews.Add((preview, offsetX, offsetY));
            }
        }
        await CreateDragPreviewAsync(card, generation); // 主卡预览 + 提示标签，内部按同一 generation 做过期检查
        if (generation != _dragGeneration || _groupDragPreviews == null) return;
        foreach (var (preview, _, _) in _groupDragPreviews)
            preview.Visibility = Visibility.Visible;
        PositionGroupPreviews();
    }

    /// <summary>渲染卡片的悬浮快照（与原卡同尺寸 × 缩放，视频卡换封面帧）。</summary>
    private async Task<Border?> SnapshotCardPreviewAsync(Border card, int generation)
    {
        // The preview lives at the root, outside both the ScrollViewer clip and sidebar.
        var snapshot = new RenderTargetBitmap();
        // 布局未收敛时 RenderAsync 会捕获"边框新尺寸 + 子元素旧 arrange"的混合状态，
        // 造成放大后的图片/音频内容只显示一半（视觉截断）：渲染前强制收敛布局。
        card.UpdateLayout();
        try { await snapshot.RenderAsync(card); }
        catch { return null; }
        if (generation != _dragGeneration) return null;
        var preview = new Border
        {
            Width = card.ActualWidth * CanvasScroll.ZoomFactor,
            Height = card.ActualHeight * CanvasScroll.ZoomFactor,
            CornerRadius = new CornerRadius(card.CornerRadius.TopLeft * CanvasScroll.ZoomFactor),
            Background = new ImageBrush { ImageSource = snapshot, Stretch = Stretch.Fill },
            Opacity = 0.94,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };
        // Swap-chain video surfaces are not captured by RenderTargetBitmap.
        if (card.DataContext is ItemVM { Type: ItemType.Video, ImageSource: not null } video)
            preview.Background = new ImageBrush { ImageSource = video.ImageSource, Stretch = Stretch.Uniform };
        return preview;
    }

    private async Task CreateDragPreviewAsync(Border card, int generation)
    {
        // The preview lives at the root, outside both the ScrollViewer clip and sidebar.
        var snapshot = new RenderTargetBitmap();
        // 同 SnapshotCardPreviewAsync：先收敛布局再拍快照，避免混合状态截断。
        card.UpdateLayout();
        try { await snapshot.RenderAsync(card); }
        catch { return; }
        if (!_pendingDragMoved || generation != _dragGeneration) return;
        _dragPreview = new Border
        {
            Width = card.ActualWidth * CanvasScroll.ZoomFactor,
            Height = card.ActualHeight * CanvasScroll.ZoomFactor,
            CornerRadius = new CornerRadius(card.CornerRadius.TopLeft * CanvasScroll.ZoomFactor),
            Background = new ImageBrush { ImageSource = snapshot, Stretch = Stretch.Fill },
            Opacity = 0.94,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };
        // Swap-chain video surfaces are not captured by RenderTargetBitmap.
        if (card.DataContext is ItemVM { Type: ItemType.Video, ImageSource: not null } video)
            _dragPreview.Background = new ImageBrush { ImageSource = video.ImageSource, Stretch = Stretch.Uniform };
        _dragLabel = new Border
        {
            Background = ResourceBrush("AccentBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = new TextBlock { Foreground = ResourceBrush("OnAccentBrush"), FontSize = 13, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis },
            Visibility = Visibility.Collapsed
        };
        Canvas.SetLeft(_dragPreview, _dragPointerRoot.X - _dragOffset.X);
        Canvas.SetTop(_dragPreview, _dragPointerRoot.Y - _dragOffset.Y);
        _dragPreview.Visibility = Visibility.Collapsed;
        DragLayer.Children.Add(_dragPreview);
        DragLayer.Children.Add(_dragLabel);
        card.Opacity = 0.22;
        _dragPreview.Visibility = Visibility.Visible;
        PositionDragPreview();
    }

    private void PositionDragPreview()
    {
        if (_dragPreview == null) return;
        Canvas.SetLeft(_dragPreview, _dragPointerRoot.X - _dragOffset.X);
        Canvas.SetTop(_dragPreview, _dragPointerRoot.Y - _dragOffset.Y);
        PositionGroupPreviews();
        if (_dragLabel == null) return;
        _dragLabel.Visibility = _canvasDragHoverCategory == null ? Visibility.Collapsed : Visibility.Visible;
        if (_dragLabel.Child is TextBlock text) text.Text = $"移入「{_canvasDragHoverCategory?.Name}」";
        Canvas.SetLeft(_dragLabel, Math.Clamp(_dragPointerRoot.X + 18, 8, Math.Max(8, RootGrid.ActualWidth - 280)));
        Canvas.SetTop(_dragLabel, Math.Clamp(_dragPointerRoot.Y + 24, 8, Math.Max(8, RootGrid.ActualHeight - 48)));
    }

    /// <summary>组内其余预览跟随主卡预览，保持按下时的相对排布。</summary>
    private void PositionGroupPreviews()
    {
        if (_dragPreview == null || _groupDragPreviews == null) return;
        var left = Canvas.GetLeft(_dragPreview);
        var top = Canvas.GetTop(_dragPreview);
        foreach (var (preview, offsetX, offsetY) in _groupDragPreviews)
        {
            Canvas.SetLeft(preview, left + offsetX);
            Canvas.SetTop(preview, top + offsetY);
        }
    }

    private void UpdateCanvasDragHover(Point point)
    {
        // Test category bounds directly; the captured pointer and preview cannot mask targets.
        // 折叠集合里的分类没有可见行，天然不作为拖放目标。
        foreach (var category in _categories)
        {
            var container = CategoryRowElement(category);
            if (container == null) continue;
            var bounds = container.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (!bounds.Contains(point)) continue;
            var panel = FindChild<Grid>(container);
            if (_canvasDragHoverCategory?.Id == category.Id) return;
            ClearCanvasDragHover();
            _canvasDragHoverCategory = category;
            _canvasDragHoverElement = panel;
            if (panel != null) panel.Background = CategoryHighlightBrush;
            return;
        }
        ClearCanvasDragHover();
    }

    private void ClearCanvasDragHover()
    {
        if (_canvasDragHoverElement != null)
            _canvasDragHoverElement.Background = _canvasDragHoverCategory?.Background ?? CategoryNormalBrush;
        _canvasDragHoverElement = null;
        _canvasDragHoverCategory = null;
    }

    private void CancelCanvasDrag()
    {
        var card = _pendingDragCard;
        // resize 会话与边缘把手样式随任何取消路径一并终止（Esc/撤回/平铺/刷新等）。
        EndResizeSession(null);
        if (_edgeHoverCard != null) ClearEdgeHover(_edgeHoverCard, stillOnCard: false);
        _pendingDragCard = null;
        _pendingDragMoved = false;
        _videoSurfacePress = false;
        _draggingCard = null;
        _dragGeneration++;
        if (card != null) { card.Opacity = 1; card.ReleasePointerCaptures(); }
        if (_pendingGroupDrag != null)
            foreach (var (member, _, _) in _pendingGroupDrag) member.Opacity = 1;
        _pendingGroupDrag = null;
        _groupDragPreviews = null;
        _dragPreview = null;
        _dragLabel = null;
        DragLayer.Children.Clear();
        ClearCanvasDragHover();
    }

    private void CanvasCard_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // resize 依赖指针捕获：捕获被夺（中键平移起手/系统手势）时立即终止，防悬空会话。
        if (_resize is { } session && ReferenceEquals(sender, session.Card))
        {
            EndResizeSession(null);
            return;
        }
        if (ReferenceEquals(sender, _pendingDragCard)) CancelCanvasDrag();
    }

    private async void CanvasCard_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // resize 释放：结束调整大小（其它指针的抬起不结束会话）。
        if (_resize is { } session)
        {
            if (e.Pointer.PointerId == session.PointerId) EndResizeSession(e);
            return;
        }
        if (_pendingDragCard is not Border card || e.Pointer.PointerId != _pendingDragPointerId) return;
        var dragged = _pendingDragMoved;
        var point = e.GetCurrentPoint(RootGrid).Position;
        if (dragged) UpdateCanvasDragHover(point);
        var category = _canvasDragHoverCategory;
        var x = _dragOrigin.X + (point.X - _pendingDragStart.X) / CanvasScroll.ZoomFactor;
        var y = _dragOrigin.Y + (point.Y - _pendingDragStart.Y) / CanvasScroll.ZoomFactor;
        var canvasPoint = e.GetCurrentPoint(CanvasScroll).Position;
        var onCanvas = canvasPoint.X >= 0 && canvasPoint.Y >= 0 && canvasPoint.X <= CanvasScroll.ActualWidth && canvasPoint.Y <= CanvasScroll.ActualHeight;
        var group = _pendingGroupDrag; // CancelCanvasDrag 会清空组引用，先取走
        var videoSurfacePress = _videoSurfacePress; // 同上：点按播放判定先取走
        CancelCanvasDrag();
        if (card.DataContext is not ItemVM vm) return;
        if (dragged)
        {
            e.Handled = true;
            try
            {
                if (category != null)
                {
                    if (group is { Count: > 0 })
                    {
                        // 整组拖到分类上：走批量归类（含单步撤销与刷新）。
                        await BatchCategorizeAsync(category.Id, category.Name);
                    }
                    else
                    {
                        var oldCategory = vm.CategoryId;
                        await Store.MoveItemAsync(vm.Id, category.Id);
                        if (oldCategory != category.Id)
                            RecordDataChange("移动到分类", () => Store.MoveItemAsync(vm.Id, oldCategory), () => Store.MoveItemAsync(vm.Id, category.Id));
                        await RefreshCategoriesAsync();
                        await RefreshItemsAsync();
                    }
                }
                else if (onCanvas)
                {
                    var before = CaptureArrangement();
                    if (group is { Count: > 0 })
                    {
                        // 整组落位：每张卡平移同一增量（各自钳制在画布内），一个历史步整体撤销。
                        var dx = (point.X - _pendingDragStart.X) / CanvasScroll.ZoomFactor;
                        var dy = (point.Y - _pendingDragStart.Y) / CanvasScroll.ZoomFactor;
                        var moved = 0;
                        foreach (var (member, memberX, memberY) in group)
                        {
                            if (member.DataContext is not ItemVM memberVm) continue;
                            var nx = Math.Clamp(memberX + dx, 0, Math.Max(0, CanvasHost.Width - member.ActualWidth));
                            var ny = Math.Clamp(memberY + dy, 0, Math.Max(0, CanvasHost.Height - member.ActualHeight));
                            var nz = ++_maxZIndex;
                            await Store.SetItemPositionAsync(memberVm.Id, nx, ny, nz);
                            memberVm.PosX = nx; memberVm.PosY = ny; memberVm.ZIndex = nz;
                            Canvas.SetLeft(member, nx); Canvas.SetTop(member, ny); Canvas.SetZIndex(member, nz);
                            moved++;
                        }
                        x = Math.Clamp(x, 0, Math.Max(0, CanvasHost.Width - card.ActualWidth));
                        y = Math.Clamp(y, 0, Math.Max(0, CanvasHost.Height - card.ActualHeight));
                        var grabbedZ = ++_maxZIndex;
                        await Store.SetItemPositionAsync(vm.Id, x, y, grabbedZ);
                        vm.PosX = x; vm.PosY = y; vm.ZIndex = grabbedZ;
                        Canvas.SetLeft(card, x); Canvas.SetTop(card, y); Canvas.SetZIndex(card, grabbedZ);
                        var afterGroup = CaptureArrangement();
                        _history.Record($"移动 {moved + 1} 个对象",
                            () => RestoreArrangementAsync(before),
                            () => RestoreArrangementAsync(afterGroup));
                    }
                    else
                    {
                        var oldX = vm.PosX; var oldY = vm.PosY; var oldZ = vm.ZIndex;
                        x = Math.Clamp(x, 0, Math.Max(0, CanvasHost.Width - card.ActualWidth));
                        y = Math.Clamp(y, 0, Math.Max(0, CanvasHost.Height - card.ActualHeight));
                        var z = ++_maxZIndex;
                        await Store.SetItemPositionAsync(vm.Id, x, y, z);
                        vm.PosX = x; vm.PosY = y; vm.ZIndex = z;
                        Canvas.SetLeft(card, x); Canvas.SetTop(card, y); Canvas.SetZIndex(card, z);
                        var after = CaptureArrangement();
                        _history.Record("移动对象",
                            () => RestoreMovedItemAsync(vm.Id, oldX, oldY, oldZ, before),
                            () => RestoreMovedItemAsync(vm.Id, x, y, z, after));
                    }
                }
            }
            catch (Exception error) { ShowImportError("未能移动灵感，请重试", error); }
        }
        else if (vm.Type == ItemType.Video && videoSurfacePress && card.Child is VideoCardView videoCard)
            videoCard.HandleSurfaceTap();
        else if (vm.Type == ItemType.Audio) ToggleMediaPlayback(vm);
        else if (vm.Type == ItemType.Image && !string.IsNullOrEmpty(vm.FilePath))
            _ = new ImageViewerWindow(Store.AbsolutePath(vm.FilePath), GetHwnd());
    }
}
