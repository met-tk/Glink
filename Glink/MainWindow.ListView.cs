using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Glink.Core;

namespace Glink;

public sealed partial class MainWindow
{
    private bool _isListMode;
    private bool _isWordCloudMode;
    private readonly List<Border> _listCards = new();
    private readonly Dictionary<Border, (double Width, double Height, double MinHeight, Thickness Padding,
        TextWrapping Wrapping, TextAlignment Alignment, HorizontalAlignment HorizontalAlignment)> _wordCloudPresentation = new();
    private StackPanel? _textListSection;
    private readonly Dictionary<Border, (UIElement Content, double Width, double Height, double MinHeight)> _textCanvasPresentation = new();
    private static readonly Brush TextListBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    // ====================================================================
    //  视图模式：紧凑平铺（默认）↔ 列表
    //  复用条目交互；文本换成紧凑行，音频按时间倒序，图片/视频进画廊。
    // ====================================================================

    // WinUI 不允许把已有父级的元素直接加入新面板（抛 COM 异常并以 0xc000027b
    // stowed-exception 崩溃），挂载前必须先从旧宿主摘除。
    private static void Reparent(Panel host, Border card)
    {
        if (card.Parent is Panel previous) previous.Children.Remove(card);
        host.Children.Add(card);
    }

    private void ViewModeChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } chip) return;
        if (chip.IsChecked != true)
        {
            SyncViewModeChips();
            return;
        }
        if (tag == "wordcloud" && _selectedType != ItemType.Text)
        {
            SyncViewModeChips();
            return;
        }
        if (tag == "wordcloud" && _isWordCloudMode || tag == "list" && _isListMode || tag == "tile" && !_isListMode && !_isWordCloudMode)
        {
            SyncViewModeChips();
            return;
        }
        SetViewMode(tag);
    }

    private void SyncViewModeChips()
    {
        SyncWordCloudAvailability();
        ViewTileButton.IsChecked = !_isListMode && !_isWordCloudMode;
        ViewListButton.IsChecked = _isListMode;
        ViewWordCloudButton.IsChecked = _isWordCloudMode;
    }

    private void SyncWordCloudAvailability()
    {
        var available = _selectedType == ItemType.Text;
        ViewWordCloudButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available && _isWordCloudMode) SetViewMode("tile");
    }

    private void SetViewMode(string mode)
    {
        EndInlineEdit(save: true);
        if (_isListMode) ReturnCardsToCanvas();
        if (_isWordCloudMode) RestoreWordCloudCards();
        _isListMode = mode == "list";
        _isWordCloudMode = mode == "wordcloud";
        ListScroll.Visibility = Visibility.Collapsed;
        CanvasScroll.Visibility = Visibility.Visible;
        if (_isWordCloudMode) ApplyWordCloudPresentation();
        ApplyCompactLayout();
        FocusCanvasContent(1f);
        _requestedZoom = 1;
        SyncViewModeChips();
        if (_isListMode)
        {
            BuildListSections();
            CanvasScroll.Visibility = Visibility.Collapsed;
            ListScroll.Visibility = Visibility.Visible;
            ListScroll.ChangeView(0, 0, 1f, disableAnimation: true);
        }
        SyncZoomLabel();
    }

    private bool IsWordCloudTextCard(Border card) =>
        _isWordCloudMode && card.DataContext is ItemVM { Type: ItemType.Text };

    /// <summary>词云模式：文本卡去框透明化，占位贴合文字本体（四周留 8px 呼吸间隙）。</summary>
    private void ApplyWordCloudPresentation()
    {
        _edgeHoverCard = null;
        SetWindowCursor(null);
        foreach (var card in CardCanvas.Children.OfType<Border>())
        {
            if (card.DataContext is not ItemVM { Type: ItemType.Text }) continue;
            if (FindChildByName<TextBlock>(card, "ContentText") is not { } text) continue;
            if (!_wordCloudPresentation.ContainsKey(card))
                _wordCloudPresentation[card] = (card.Width, card.Height, card.MinHeight, card.Padding,
                    text.TextWrapping, text.TextAlignment, text.HorizontalAlignment);
            card.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            card.BorderThickness = new Thickness(0);
            card.Padding = new Thickness(8);
            card.MinHeight = 0;
            card.Height = double.NaN;
            text.Tag = null;
            RestoreText(text, FindChildByName<TextBlock>(card, "ContentMeta"));
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextAlignment = TextAlignment.Left;
            text.HorizontalAlignment = HorizontalAlignment.Left;
            if (FindChildByName<TextBlock>(card, "ContentMeta") is { } meta) meta.Visibility = Visibility.Collapsed;
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Width = Math.Ceiling(text.DesiredSize.Width) + 16;
        }
        CardCanvas.UpdateLayout();
    }

    private void RestoreWordCloudCards()
    {
        foreach (var card in CardCanvas.Children.OfType<Border>())
        {
            if (card.DataContext is not ItemVM { Type: ItemType.Text } vm) continue;
            if (FindChildByName<TextBlock>(card, "ContentText") is not { } text) continue;
            card.Background = ResourceBrush("CardBrush");
            card.BorderBrush = vm.CategoryId.HasValue ? CardCategoryBrush(vm) : ResourceBrush("BorderBrush");
            card.BorderThickness = new Thickness(vm.CategoryId.HasValue ? 2 : 1);
            if (_wordCloudPresentation.Remove(card, out var presentation))
            {
                card.Width = presentation.Width;
                card.Height = presentation.Height;
                card.MinHeight = presentation.MinHeight;
                card.Padding = presentation.Padding;
                text.TextWrapping = presentation.Wrapping;
                text.TextAlignment = presentation.Alignment;
                text.HorizontalAlignment = presentation.HorizontalAlignment;
            }
            else
            {
                card.Padding = new Thickness(20);
                card.MinHeight = 116;
                text.TextWrapping = TextWrapping.Wrap;
            }
            text.Tag = null;
            RestoreText(text, FindChildByName<TextBlock>(card, "ContentMeta"));
            if (double.IsNaN(card.Width)) SizeTextCard(card, (FrameworkElement)card.Child);
        }
    }


    /// <summary>右下角缩放标签：随当前视图模式显示对应 ScrollViewer 的倍率。</summary>
    private void SyncZoomLabel()
    {
        if (ZoomResetBtn == null) return;
        var zoom = _isListMode ? ListScroll.ZoomFactor : CanvasScroll.ZoomFactor;
        var label = $"{zoom:P0}";
        ZoomResetBtn.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ZoomResetBtn, $"缩放 {label}，点击恢复原始大小");
    }

    private void ListScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isListMode) return;
        SyncZoomLabel();
    }

    private void ListScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTextListWidth();

    private void UpdateTextListWidth()
    {
        // 只约束文本行，媒体画廊仍可横向滚动；放大后文本也能平移。
        if (_textListSection != null)
            _textListSection.Width = Math.Max(0, ListScroll.ActualWidth - ListScroll.Padding.Left - ListScroll.Padding.Right);
    }

    private bool IsTextListRow(Border card) => _textCanvasPresentation.ContainsKey(card);

    private static string TextListPreview(string value) => string.IsNullOrWhiteSpace(value)
        ? "空白文本"
        : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private FrameworkElement BuildTextListRow(ItemVM vm, Grid textGrid)
    {
        var text = (TextBlock)textGrid.Children[0];
        text.Text = TextListPreview(vm.Content ?? string.Empty);
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxLines = 1;
        var row = new Grid { ColumnSpacing = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(textGrid);
        var meta = new TextBlock
        {
            Name = "ContentMeta", Text = vm.MetaText,
            Style = (Style)Application.Current.Resources["CardMetaStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(meta, 1);
        row.Children.Add(meta);
        var content = new ContentControl
        {
            Content = row, IsTabStop = true, UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(content, text.Text);
        AutomationProperties.SetHelpText(content, "双击或按 Enter 编辑全文");
        ToolTipService.SetToolTip(content, "双击查看或编辑全文");
        content.KeyDown += (_, e) =>
        {
            if (_editingItem != null) return;
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.F2)
            {
                StartInlineEdit(vm);
                e.Handled = true;
            }
        };
        return content;
    }

    private void ApplyTextListVisual(Border card, bool selected, bool hovering = false)
    {
        card.BorderBrush = ResourceBrush(selected ? "AccentBrush" : "BorderBrush");
        card.BorderThickness = new Thickness(0, 0, 0, 1);
        card.Background = selected ? ResourceBrush("SelectionBrush")
            : hovering ? ResourceBrush("CardHoverBrush") : TextListBackground;
    }

    private void UseTextListPresentation(Border card)
    {
        var vm = (ItemVM)card.DataContext;
        ClearEdgeHover(card, stillOnCard: false);
        _textCanvasPresentation[card] = (card.Child, card.Width, card.Height, card.MinHeight);
        card.Child = BuildCardContent(vm, textListRow: true);
        card.Height = double.NaN;
        card.MinHeight = 48;
        card.Padding = new Thickness(8, 12, 8, 12);
        card.CornerRadius = new CornerRadius(0);
        card.RenderTransform = null;
        ApplyTextListVisual(card, IsCardSelected(card));
    }

    /// <summary>把画布上的卡片按类型编组挂进列表宿主（时间倒序）。</summary>
    private void BuildListSections()
    {
        EndInlineEdit(save: true);
        CancelCanvasDrag();
        // 幂等：先把上一轮悬挂在列表里的卡还回画布再重新编组。
        ReturnCardsToCanvas();
        _listCards.Clear();
        ListSections.Children.Clear();

        var cards = CardCanvas.Children.OfType<Border>().ToList();
        if (cards.Count == 0) return;

        void AddListSection(string title, IEnumerable<Border> rows, bool textRows = false)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            var section = new StackPanel { Spacing = textRows ? 8 : 26 };
            if (textRows)
            {
                _textListSection = section;
                section.HorizontalAlignment = HorizontalAlignment.Left;
            }
            section.Children.Add(new TextBlock
            {
                Text = $"{title} · {list.Count} 条",
                Style = (Style)Application.Current.Resources["SidebarLabelStyle"]
            });
            var host = new StackPanel { Spacing = textRows ? 0 : 10 };
            foreach (var card in list)
            {
                if (textRows) UseTextListPresentation(card);
                card.Width = double.NaN;
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                Reparent(host, card);
                _listCards.Add(card);
            }
            section.Children.Add(host);
            ListSections.Children.Add(section);
        }

        void AddGallerySection(string title, IEnumerable<Border> cells)
        {
            var list = cells.ToList();
            if (list.Count == 0) return;
            ListSections.Children.Add(new TextBlock
            {
                Text = $"{title} · {list.Count} 项",
                Style = (Style)Application.Current.Resources["SidebarLabelStyle"]
            });
            var gallery = new WrapPanel { Spacing = 12 };
            foreach (var card in list)
            {
                Reparent(gallery, card);
                _listCards.Add(card);
            }
            ListSections.Children.Add(gallery);
        }

        static IEnumerable<Border> Ordered(IEnumerable<Border> source) => source
            .OrderByDescending(c => (c.DataContext as ItemVM)?.CreatedAt ?? DateTime.MinValue);

        AddListSection("文本", Ordered(cards.Where(IsType(ItemType.Text))), textRows: true);
        AddListSection("音频", Ordered(cards.Where(IsType(ItemType.Audio))));
        AddGallerySection("图片", Ordered(cards.Where(IsType(ItemType.Image))));
        AddGallerySection("视频", Ordered(cards.Where(IsType(ItemType.Video))));
        UpdateTextListWidth();

        static Func<Border, bool> IsType(ItemType type) =>
            card => (card.DataContext as ItemVM)?.Type == type;
    }

    /// <summary>列表宿主清空重建前的复位：旧卡随整轮重载一并废弃。</summary>
    private void ResetListHost()
    {
        _textListSection = null;
        _textCanvasPresentation.Clear();
        _listCards.Clear();
        ListSections.Children.Clear();
    }

    /// <summary>把悬挂在列表宿主里的卡片归还画布并恢复画布卡尺寸。</summary>
    private void ReturnCardsToCanvas()
    {
        _textListSection = null;
        if (_listCards.Count == 0) return;
        foreach (var card in _listCards)
        {
            if (_textCanvasPresentation.Remove(card, out var saved))
            {
                card.Child = saved.Content;
                card.Width = saved.Width;
                card.Height = saved.Height;
                card.MinHeight = saved.MinHeight;
                card.Padding = new Thickness(20);
                card.ClearValue(Border.CornerRadiusProperty);
                card.Background = ResourceBrush("CardBrush");
                card.HorizontalAlignment = HorizontalAlignment.Left;
                ApplyCardSelectionVisual(card, IsCardSelected(card));
                if (FindChildByName<TextBlock>(card, "ContentText") is { } text)
                {
                    text.Text = ((ItemVM)card.DataContext).Content ?? string.Empty;
                    text.Tag = null;
                    RestoreText(text, FindChildByName<TextBlock>(card, "ContentMeta"));
                }
            }
            else if (card.DataContext is ItemVM vm)
            {
                card.Width = vm.Type == ItemType.Video ? 480 : 360;
                card.HorizontalAlignment = HorizontalAlignment.Left;
            }
            Reparent(CardCanvas, card);
        }
        _listCards.Clear();
        ListSections.Children.Clear();
    }

    // ====================================================================
    //  沉浸模式：只保留画布/列表区（隐藏标题栏、侧栏、内容头、筛选行），
    //  默认保持窗口形态；全屏是独立的选择（F11 随时切换，可与沉浸叠加）。
    //  Esc 或右下角按钮退出沉浸。
    // ====================================================================

    private bool _isImmersive;

    private void ImmersiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isImmersive) ExitImmersive();
        else EnterImmersive();
    }

    private void EnterImmersive()
    {
        if (_isImmersive) return;
        _isImmersive = true;
        EndInlineEdit(save: true);
        CloseSettings();
        TitleBarRegion.Visibility = Visibility.Collapsed;
        RootGrid.RowDefinitions[0].Height = new GridLength(0);
        RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
        SidebarRegion.Visibility = Visibility.Collapsed;
        ContentHeader.Visibility = Visibility.Collapsed;
        FilterRow.Visibility = Visibility.Collapsed;
        ImmersiveIcon.Glyph = "\uE73E";
        ToolTipService.SetToolTip(ImmersiveButton, "退出沉浸模式 (Esc)");
    }

    private void ExitImmersive()
    {
        if (!_isImmersive) return;
        _isImmersive = false;
        TitleBarRegion.Visibility = Visibility.Visible;
        RootGrid.RowDefinitions[0].Height = new GridLength(40);
        RootGrid.ColumnDefinitions[0].Width = new GridLength(240);
        SidebarRegion.Visibility = Visibility.Visible;
        ContentHeader.Visibility = Visibility.Visible;
        FilterRow.Visibility = Visibility.Visible;
        ImmersiveIcon.Glyph = "\uE740";
        ToolTipService.SetToolTip(ImmersiveButton, "沉浸模式 (Esc 退出) · F11 全屏");
        UpdateWindowLayout();
    }

    // ====================================================================
    //  F11 全屏：窗口形态切换，独立于沉浸模式（沉浸中也可用）。
    // ====================================================================

    private bool _wasMaximizedBeforeFullScreen;

    private void ToggleFullScreen()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_wasMaximizedBeforeFullScreen) ShowWindow(hwnd, SW_MAXIMIZE); // 全屏 presenter 会吞掉最大化状态
            }
            else
            {
                _wasMaximizedBeforeFullScreen = IsZoomed(hwnd);
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
        }
        catch { /* 形态切换失败静默 */ }
    }
}
