using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Glink.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Glink;

// ====================================================================
//  ViewModels
// ====================================================================

public sealed class CategoryVM : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(Background))); }
    }
    // 共享透明画刷：Background 绑定在拖拽重排动画期间会被高频重取，
    // 每次新建实例会在 XAML 侧引发 stowed exception 崩溃（参考 microsoft-ui-xaml #5607）。
    private static readonly Brush TransparentCategoryBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    // 选中色调也缓存（分类色 × 当前主题 双键失效）：高频重取返回同一实例。
    private Brush? _selectedTintCache;
    private string? _selectedTintKey;
    public Brush Background => IsSelected ? SelectedTint : TransparentCategoryBrush;
    private Brush SelectedTint
    {
        get
        {
            var key = ThemeService.Current.Id + "|" + AccentHex;
            if (_selectedTintCache == null || _selectedTintKey != key)
            {
                var card = ((SolidColorBrush)Application.Current.Resources["CardBrush"]).Color;
                _selectedTintCache = new SolidColorBrush(ThemeService.Mix(card, HexToColor(AccentHex), 0.22));
                _selectedTintKey = key;
            }
            return _selectedTintCache;
        }
    }
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    private string _accentHex = "#E8956B";
    public string AccentHex
    {
        get => _accentHex;
        set { _accentHex = value; PropertyChanged?.Invoke(this, new(nameof(AccentBrush))); PropertyChanged?.Invoke(this, new(nameof(Background))); }
    }
    public int Count { get; set; }
    public string CountText => Count > 0 ? Count.ToString() : string.Empty;
    public SolidColorBrush AccentBrush => new SolidColorBrush(HexToColor(AccentHex));

    public static Windows.UI.Color HexToColor(string hex)
    {
        hex = hex.Replace("#", string.Empty);
        if (hex.Length == 6)
            hex = "FF" + hex;
        var r = Convert.ToByte(hex.Substring(2, 2), 16);
        var g = Convert.ToByte(hex.Substring(4, 2), 16);
        var b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }
}

public sealed class ItemVM : System.ComponentModel.INotifyPropertyChanged
{
    public Guid Id { get; set; }
    public ItemType Type { get; set; }
    public string? Content { get; set; }
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsFavorite { get; set; }
    public Guid? CategoryId { get; set; }
    public double? PosX { get; set; }
    public double? PosY { get; set; }
    public int ZIndex { get; set; }
    public double VideoAspectRatio { get; set; } = 16.0 / 9;
    public TimeSpan VideoDuration { get; set; }

    public string MetaText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (IsFavorite) parts.Add("★");
            parts.Add(CreatedAt.ToString("MM/dd HH:mm"));
            return string.Join("  ", parts);
        }
    }

    public string FileNameText => string.IsNullOrEmpty(FileName) ? "音频" : FileName;

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(PlayGlyph));
            }
        }
    }

    public string PlayGlyph => IsPlaying ? "\uE103" : "\uE768";

    private BitmapImage? _imageSource;
    public BitmapImage? ImageSource
    {
        get => _imageSource;
        set
        {
            if (_imageSource != value)
            {
                _imageSource = value;
                OnPropertyChanged(nameof(ImageSource));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

// ====================================================================
//  DataTemplateSelector
// ====================================================================


// ====================================================================
//  MainWindow
// ====================================================================

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<CategoryVM> _categories = new();

    private readonly ExternalDropImporter _dropImporter = new();
    private readonly Task _storeInitialization;
    private readonly DispatcherQueueTimer _dropLeaveTimer;
    private bool _isImporting;
    private const string InternalDragFormat = "Glink.ItemId";
    private Point? _lastMouseCanvasPoint;

    private static readonly string[] AccentPalette =
        { "#E8956B", "#D97BA6", "#8FBF7F", "#7BA6D9", "#B39DDB", "#6BC5C5", "#D9C97B", "#C58B6B" };

    private Guid? _selectedCategoryId = null;  // null = all
    private string? _selectedTimeFilter = null; // null = all
    private ItemType? _selectedType = null;      // null = all

    private readonly MediaPlayer _mediaPlayer;
    private ItemVM? _playingItem;
    private readonly DispatcherQueueTimer _progressTimer;

    // Feature 1: drag item to sidebar category

    // Canvas mode: the category VM currently hovered during a canvas drag
    private CategoryVM? _canvasDragHoverCategory;
    private Panel? _canvasDragHoverElement;

    // Feature 2: canvas mode (default)

    private int _maxZIndex = 0;
    private Border? _draggingCard;
    private Border? _pendingDragCard;   // 按下但尚未判定为拖拽的卡片
    private Point _pendingDragStart;
    private bool _pendingDragMoved;
    private uint _pendingDragPointerId;

    // 布局动画过渡状态
    private DispatcherTimer? _layoutAnimationTimer;
    private readonly List<(Border Card, ItemVM? Vm, double StartX, double StartY, double TargetX, double TargetY)> _activeTransitions = new();
    private double _transitionElapsedMs;
    private const double TransitionDurationMs = 280.0;

    public MainWindow()
    {
        InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Observe even events handled by a ListView/TextBox, but import only once at the root.
        RootGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ContentArea_DragOver), true);
        RootGrid.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(ContentArea_DragOver), true);
        RootGrid.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(ContentArea_DragLeave), true);
        RootGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(ContentArea_Drop), true);
        _dropLeaveTimer = _dispatcher.CreateTimer();
        _dropLeaveTimer.Interval = TimeSpan.FromMilliseconds(100);
        _dropLeaveTimer.IsRepeating = false;
        _dropLeaveTimer.Tick += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Closed += (_, _) => _dropLeaveTimer.Stop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        InitializeAppearance();
        InitializeCommands();
        InitializeShortcuts();
        // The toast reads as one of the app's floating cards, not a stock info strip.
        ImportStatus.CornerRadius = new CornerRadius(10);
        ImportStatus.BorderThickness = new Thickness(1);
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), true);
        // 空格在预览（隧道）阶段路由到可播放元素，先于任何焦点元素的默认空格激活。
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;
        // 键盘事件仍以 handled-events-too 挂根节点（快捷键录制等链路依赖看到已处理按键）。
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
        // 滚轮挂在 CanvasHost（ScrollViewer 内容层）：交互中的 Ctrl+滚轮要在
        // 原生缩放处理之前拦下；正常缩放放行给 ScrollViewer 原生完成。
        CanvasHost.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ContentArea_PointerWheelChanged), true);
        // 中键平移挂 CanvasScroll 且收听已处理事件：视频表面、内联编辑框等子控件
        // 会把 PointerPressed 标记为已处理，普通挂接收不到，导致视频上无法起手平移。
        CanvasScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(CanvasScroll_PointerPressed), true);
        CanvasScroll.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(CanvasScroll_PointerEntered), true);
        CanvasScroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(CanvasScroll_PointerMoved), true);
        CanvasScroll.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(CanvasScroll_PointerExited), true);
        CanvasScroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(CanvasScroll_PointerReleased), true);
        InitializeSelection();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) EndInlineEdit(save: true);
        };
        AppWindow.Closing += async (_, args) =>
        {
            EndInlineEdit(save: true);
            if (_exportCancellation is { } exporting)
            {
                args.Cancel = true;
                exporting.Cancel();
                try { await _exportCompletion; }
                catch { /* 导出结果由导出流程报告；关闭仍继续。 */ }
                if (!_isClosed) Close();
                return;
            }
            if (_textSaveTask.IsCompleted && _unsavedText.Count == 0) return;
            args.Cancel = true;
            await _textSaveTask;
            if (_unsavedText.Count == 0) Close();
        };

        RestoreWindowPlacement();
        // Maximize() during early startup races with the framework applying the
        // window's own initial size, so wait for the first activation instead.
        Activated += (_, _) =>
        {
            if (!_pendingMaximize) return;
            _pendingMaximize = false;
            (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
        };
        AppWindow.Changed += (s, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
                _dispatcher.TryEnqueue(RememberPlacement);
        };

        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaEnded += OnMediaEnded;

        _progressTimer = _dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(100);
        _progressTimer.Tick += (s, a) => ProgressTimer_Tick();
        Closed += (_, _) =>
        {
            _isClosed = true;
            SaveWindowPlacement();
            DisposeVideoCards();
            _progressTimer.Stop();
            _mediaPlayer.Dispose();
        };

        _storeInitialization = Store.InitializeAsync();
        _ = InitializeAsync();
    }

    // ====================================================================
    //  Initialization
    // ====================================================================

    private async Task InitializeAsync()
    {
        try
        {
            await _storeInitialization;
            await RefreshCategoriesAsync();
            await RefreshItemsAsync();
        }
        catch (Exception error)
        {
            ShowImportError("无法打开灵感库", error);
        }
    }

    // ====================================================================
    //  Data refresh
    // ====================================================================

    private async Task RefreshCategoriesAsync()
    {
        try
        {
            var cats = await Store.GetCategoriesAsync();
            // 显示顺序：拖拽排序结果（旁路文件）优先，未记录的按 Core 的 SortOrder 追加。
            var savedOrder = CategoryOrderService.Load();
            var ordered = cats
                .OrderBy(c => { var index = savedOrder.IndexOf(c.Id); return index < 0 ? int.MaxValue : index; })
                .ThenBy(c => c.SortOrder)
                .ToList();
            _categories.Clear();
            foreach (var c in ordered)
            {
                var count = await Store.GetItemCountAsync(c.Id);
                _categories.Add(new CategoryVM
                {
                    Id = c.Id,
                    Name = c.Name,
                    AccentHex = c.AccentHex,
                    Count = count,
                    IsSelected = c.Id == _selectedCategoryId
                });
            }

            var totalCount = await Store.GetItemCountAsync("all");
            AllCountText.Text = totalCount.ToString();
            FavoritesCountText.Text = (await Store.GetItemCountAsync("favorites")).ToString();

            if (_selectedCategoryId is not null
                && cats.FirstOrDefault(x => x.Id == _selectedCategoryId.Value) == null)
                _selectedCategoryId = null;
            // 分类行（含集合结构）由 Categories.cs 构建，顺序以 category-groups.json 为准。
            BuildCategoryRows();
        }
        catch
        {
            // Silent degradation
        }
    }

    private async Task RefreshItemsAsync()
    {
        try
        {
            SyncWordCloudAvailability();
            EndInlineEdit(save: true);
            await _textSaveTask;
            await LoadCanvasAsync();
            UpdateContentTitle();
            AnimateTitleFadeIn();
        }
        catch (Exception error) { ShowImportError("无法加载灵感", error); }
    }

    private void UpdateContentTitle()
    {
        var preset = ActiveLayoutPreset;
        var name = preset?.Name ?? (_favoritesOnly ? "收藏" : "全部灵感");
        if (preset == null && _selectedCategoryId is Guid selId)
        {
            var cat = _categories.FirstOrDefault(x => x.Id == selId);
            if (cat != null) name = cat.Name;
            TitleDot.Fill = new SolidColorBrush(CategoryVM.HexToColor(cat?.AccentHex ?? "#E8956B"));
        }
        else
        {
            TitleDot.Fill = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        }

        ContentTitle.Text = name;
        AllHighlight.Visibility = preset == null && !_favoritesOnly && _selectedCategoryId == null ? Visibility.Visible : Visibility.Collapsed;
        FavoritesHighlight.Visibility = preset == null && _favoritesOnly ? Visibility.Visible : Visibility.Collapsed;
        // 列表模式下卡片不在画布上（挂 ListSections），两处合计才是真实数量，
        // 否则切分类会误判为空、空状态浮层会盖在列表上。
        var count = CardCanvas.Children.Count + _listCards.Count;
        ContentSubtitle.Text = $"{count} 个灵感";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var filtered = _selectedType != null || _selectedTimeFilter != null;
        EmptyTitle.Text = filtered ? "没有符合筛选的灵感" : "留一个位置，给新的灵感";
        EmptyHint.Text = filtered ? "试试其他类型，或切换到全部时间。" : "拖入或粘贴图片、音频或视频，也可以写下一段文字。";
        if (_favoritesOnly && !filtered)
        {
            EmptyTitle.Text = "把想常看的灵感收藏在这里";
            EmptyHint.Text = "右键任意灵感，选择「收藏」，即可在这里找到它。";
        }
        foreach (var category in _categories) category.IsSelected = category.Id == _selectedCategoryId;
        UpdatePresetRowHighlights();
    }

    // ====================================================================
    //  Sidebar: All + Category navigation
    // ====================================================================

    private void AllButton_Click(object sender, RoutedEventArgs e)
    {
        _favoritesOnly = false;
        _selectedCategoryId = null;
        _selectedPresetId = null;
        AllHighlight.Visibility = Visibility.Visible;
        _ = RefreshItemsAsync();
    }

    // ====================================================================
    //  Filter chips
    // ====================================================================

    private void TypeChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        var tag = btn.Tag?.ToString() ?? "all";

        // Deselect siblings
        foreach (var tb in new[] { ChipAllType, ChipText, ChipImage, ChipAudio, ChipVideo })
        {
            if (tb != btn) tb.IsChecked = false;
        }

        // If unchecked, fall back to all
        if (btn.IsChecked != true)
        {
            ChipAllType.IsChecked = true;
            _selectedType = null;
        }
        else
        {
            _selectedType = tag switch
            {
                "text" => ItemType.Text,
                "image" => ItemType.Image,
                "audio" => ItemType.Audio,
                "video" => ItemType.Video,
                _ => null
            };
        }

        _ = RefreshItemsAsync();
    }

    private void TimeChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        var tag = btn.Tag?.ToString() ?? "all";

        // Deselect siblings
        foreach (var tb in new[] { ChipAllTime, ChipToday, ChipWeek, ChipEarlier })
        {
            if (tb != btn) tb.IsChecked = false;
        }

        // If unchecked, fall back to all
        if (btn.IsChecked != true)
        {
            ChipAllTime.IsChecked = true;
            _selectedTimeFilter = null;
        }
        else
        {
            _selectedTimeFilter = tag switch
            {
                "today" => "today",
                "week" => "week",
                "earlier" => "earlier",
                _ => null
            };
        }

        _ = RefreshItemsAsync();
    }

    // ====================================================================
    //  Add inspiration: Flyout → Text / File
    // ====================================================================

    private async void AddText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AddInspirationBtn.Flyout.Hide();

            var tb = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 160,
                MaxHeight = 300,
                PlaceholderText = "写下此刻的灵感…",
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                Background = ResourceBrush("SidebarBrush"),
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                Padding = new Thickness(12)
            };

            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "写一段文字",
                Content = tb,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Background = (Brush)Application.Current.Resources["CardBrush"],
                Foreground = (Brush)Application.Current.Resources["TextBrush"]
            };

            var result = await ThemeService.ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(tb.Text))
            {
                var added = await Store.AddTextAsync(tb.Text.Trim(), _selectedCategoryId);
                RecordAddedItems(new[] { added });
                _favoritesOnly = false;
                await RefreshCategoriesAsync();
                await RefreshItemsAsync();
            }
        }
        catch
        {
            // Silent degradation
        }
    }

    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting) return;
        var categoryId = _selectedCategoryId;
        _isImporting = true;
        AddInspirationBtn.IsEnabled = false;
        try
        {
            AddInspirationBtn.Flyout.Hide();
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, GetHwnd());
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var extension in Store.SupportedFileExtensions)
                picker.FileTypeFilter.Add(extension);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;
            await _storeInitialization;
            var result = await _dropImporter.ImportFilesAsync(files, categoryId, ShowImportProgress);
            await FinishImportAsync(result, categoryId);
        }
        catch (Exception error)
        {
            ShowImportError("文件添加失败", error);
        }
        finally
        {
            _isImporting = false;
            AddInspirationBtn.IsEnabled = true;
        }
    }
    private void AddInspirationBtn_Click(object sender, RoutedEventArgs e)
    {
        // Flyout is declared in XAML, it opens automatically
    }

    // ====================================================================
    //  New category / Rename / Delete category
    // ====================================================================

    private void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        // 默认色随分类数轮转，用户可在弹层里改选。
        var defaultColor = CategoryPalette[_categories.Count % CategoryPalette.Length].Hex;
        ShowCategoryEditFlyout(NewCategoryButton, "新建分类", "创建", string.Empty, defaultColor,
            async (name, color) =>
            {
                var category = await Store.AddCategoryAsync(name, color);
                RecordDataChange("新建分类", () => Store.DeleteCategoryAsync(category.Id), () => Store.RestoreCategoryAsync(category, Array.Empty<Guid>()));
                await RefreshCategoriesAsync();
            });
    }

    // ====================================================================
    //  Category right-tap / rename / delete → 由 MainWindow.Categories.cs
    //  的 CategoryRow_RightTapped 代码菜单承担（行不再由 ListView 模板生成）。
    // ====================================================================

    // ====================================================================
    //  Card interactions: click (play media), right-tap (menu)
    // ====================================================================


    private void ToggleMediaPlayback(ItemVM vm)
    {
        try
        {
            if (vm.Type != ItemType.Audio) return;
            PauseVideos();
            if (string.IsNullOrEmpty(vm.FilePath)) return;
            var absPath = Store.AbsolutePath(vm.FilePath);

            if (_playingItem == vm)
            {
                if (_mediaPlayer.CurrentState == MediaPlayerState.Playing)
                {
                    _mediaPlayer.Pause();
                    vm.IsPlaying = false;
                    _progressTimer.Stop();
                }
                else
                {
                    _mediaPlayer.Play();
                    vm.IsPlaying = true;
                    _progressTimer.Start();
                }
                return;
            }

            // Stop previous
            if (_playingItem != null)
            {
                var prevCanvas = FindWaveformCanvasForItem(_playingItem);
                if (prevCanvas != null)
                    RedrawWaveform(prevCanvas, _playingItem, 0);

                _playingItem.IsPlaying = false;
            }

            var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(absPath));
            _pendingSeekRatio = null;
            _mediaPlayer.Source = source;
            _mediaPlayer.Play();
            _playingItem = vm;
            vm.IsPlaying = true;
            _progressTimer.Start();
        }
        catch
        {
            // Silent degradation
        }
    }

    private void ProgressTimer_Tick()
    {
        try
        {
            if (_playingItem == null) return;
            ApplyPendingAudioSeek();
            var duration = _mediaPlayer.PlaybackSession?.NaturalDuration.TotalSeconds ?? 0;
            var pos = _mediaPlayer.PlaybackSession?.Position.TotalSeconds ?? 0;
            var progress = duration > 0 ? Math.Clamp(pos / duration, 0.0, 1.0) : 0;

            var canvas = FindWaveformCanvasForItem(_playingItem);
            if (canvas != null)
                RedrawWaveform(canvas, _playingItem, progress);
        }
        catch { /* Silent */ }
    }

    private Canvas? FindWaveformCanvasForItem(ItemVM item)
    {
        var container = FindCardContainer(item);
        return container == null ? null : FindChild<Canvas>(container);
    }

    /// <summary>波形重画用的当前进度：正在播的卡取真实播放位置，其余为 0。</summary>
    private double WaveformProgressFor(ItemVM vm)
    {
        if (_playingItem != vm) return 0;
        try
        {
            var duration = _mediaPlayer.PlaybackSession?.NaturalDuration.TotalSeconds ?? 0;
            var pos = _mediaPlayer.PlaybackSession?.Position.TotalSeconds ?? 0;
            return duration > 0 ? Math.Clamp(pos / duration, 0.0, 1.0) : 0;
        }
        catch { return 0; }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var result = FindChildByName<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _progressTimer.Stop();
            if (_playingItem != null)
            {
                var canvas = FindWaveformCanvasForItem(_playingItem);
                if (canvas != null)
                    RedrawWaveform(canvas, _playingItem, 0);

                _playingItem.IsPlaying = false;
                _playingItem = null;
            }
        });
    }

    // ====================================================================
    //  Card right-tap → MenuFlyout
    // ====================================================================

    private void Card_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            // 多张选中且右键落在选中卡上：批量菜单（删除/归类/收藏）。
            if (TryShowMultiSelectMenu(fe, e.GetPosition(fe))) { e.Handled = true; return; }
            // 右键落在未选中的卡片上：回到单卡菜单语境，旧多选不再有意义。
            if (HasSelection && fe is Border b && !IsCardSelected(b)) ClearSelection();
            var flyout = FlyoutBase.GetAttachedFlyout(fe);
            if (flyout is MenuFlyout menu)
            {
                // Ensure each menu item's DataContext points to the ItemVM
                if (fe.DataContext is ItemVM vm)
                {
                    foreach (var mi in menu.Items)
                    {
                        if (mi is MenuFlyoutItem mfi)
                            mfi.DataContext = vm;
                    }
                }
                flyout.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
            }
        }
    }

    // ====================================================================
    //  Card menu actions
    // ====================================================================

    private async void FavoriteItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuFlyoutItem item) return;
            if (item.DataContext is not ItemVM vm) return;
            await Store.ToggleFavoriteAsync(vm.Id);
            RecordDataChange(vm.IsFavorite ? "取消收藏" : "收藏", () => Store.ToggleFavoriteAsync(vm.Id), () => Store.ToggleFavoriteAsync(vm.Id));
            await RefreshCategoriesAsync();
            await RefreshItemsAsync();
        }
        catch (Exception error) { ShowImportError("未能更新收藏", error); }
    }

    private void CopyTextItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuFlyoutItem item) return;
            if (item.DataContext is not ItemVM vm) return;
            if (string.IsNullOrEmpty(vm.Content)) return;

            var dp = new DataPackage();
            dp.SetText(vm.Content);
            Clipboard.SetContent(dp);
        }
        catch { /* Silent */ }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuFlyoutItem item) return;
            if (item.DataContext is not ItemVM vm) return;

            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "删除灵感",
                Content = new TextBlock { Text = "确定删除这条灵感？", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Background = (Brush)Application.Current.Resources["CardBrush"],
                Foreground = (Brush)Application.Current.Resources["TextBrush"]
            };

            var result = await ThemeService.ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                EndInlineEdit(save: true); await _textSaveTask;
                var snapshot = (await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == vm.Id);
                ReleaseMediaForHistory();
                var retained = new ArchivedItems(new[] { snapshot });
                await retained.RemoveAsync();
                RecordDataChange("删除灵感", retained.RestoreAsync, retained.RemoveAsync, retained);
                await RefreshCategoriesAsync();
                await RefreshItemsAsync();
            }
        }
        catch (Exception error) { ShowImportError("未能删除灵感，请重试", error); await RefreshItemsAsync(); }
    }

    // ====================================================================
    //  Card animations: Loaded fade-in + hover
    // ====================================================================

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (IsWordCloudTextCard(card)) return;
        if (IsTextListRow(card))
        {
            ApplyTextListVisual(card, IsCardSelected(card), hovering: true);
            return;
        }
        if (_pendingDragMoved) return;
        var tt = card.RenderTransform as TranslateTransform;
        if (tt == null)
        {
            tt = new TranslateTransform();
            card.RenderTransform = tt;
        }
        var sb = new Storyboard();
        var ty = new DoubleAnimation
        {
            To = -2, Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(ty, card);
        Storyboard.SetTargetProperty(ty, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        sb.Children.Add(ty);
        sb.Begin();

        card.BorderBrush = BorderBrushForCard(card, hovering: true);
        // 边框提亮 + 背景向强调色轻移，与上浮动画共同构成"拿起"反馈。
        card.Background = ResourceBrush("CardHoverBrush");
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (IsWordCloudTextCard(card)) return;
        if (IsTextListRow(card))
        {
            ApplyTextListVisual(card, IsCardSelected(card));
            return;
        }
        if (_pendingDragMoved) return;
        // 离开卡片：边缘把手样式先还原（含边框厚度），再走常规悬停恢复。
        ClearEdgeHover(card, stillOnCard: false);
        SetWindowCursor(null);
        var tt = card.RenderTransform as TranslateTransform;
        if (tt == null)
        {
            tt = new TranslateTransform();
            card.RenderTransform = tt;
        }
        var sb = new Storyboard();
        var ty = new DoubleAnimation
        {
            To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(ty, card);
        Storyboard.SetTargetProperty(ty, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        sb.Children.Add(ty);
        sb.Begin();

        card.BorderBrush = BorderBrushForCard(card, hovering: false);
        card.Background = ResourceBrush("CardBrush");
    }

    // ====================================================================
    //  Title fade-in on category switch
    // ====================================================================

    private void AnimateTitleFadeIn()
    {
        var panel = ContentTitle.Parent as FrameworkElement;
        if (panel == null) return;

        var fade = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, ContentTitle);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }

    // ====================================================================
    //  Text edit (in-place, inside the card itself)
    // ====================================================================

    private ItemVM? _editingItem;

    private void EditTextItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuFlyoutItem item) return;
            if (item.DataContext is not ItemVM vm) return;
            StartInlineEdit(vm);
        }
        catch { /* Silent */ }
    }

    private Task _textSaveTask = Task.CompletedTask;
    private TextBox? _activeEditBox;
    private bool _isComposingText;
    private readonly Dictionary<Guid, string> _unsavedText = new();

    private DependencyObject? FindCardContainer(ItemVM vm)
    {
        // 列表模式下卡片不在画布里，改查列表编组的登记表。
        if (_isListMode)
            return _listCards.FirstOrDefault(b => b.DataContext is ItemVM item && item.Id == vm.Id);
        return CardCanvas.Children.OfType<Border>()
            .FirstOrDefault(b => b.DataContext is ItemVM item && item.Id == vm.Id);
    }

    private void StartInlineEdit(ItemVM vm)
    {
        if (_editingItem?.Id == vm.Id) return;
        EndInlineEdit(save: true);
        var container = FindCardContainer(vm);
        if (container == null) return;
        var editBox = FindChildByName<TextBox>(container, "EditBox");
        if (editBox == null) return;
        _editingItem = vm;
        _activeEditBox = editBox;
        var text = FindChildByName<TextBlock>(container, "ContentText");
        if (text != null) text.Visibility = Visibility.Collapsed;
        _isComposingText = false;
        editBox.Text = _unsavedText.GetValueOrDefault(vm.Id, vm.Content ?? string.Empty);
        editBox.Visibility = Visibility.Visible;
        editBox.Focus(FocusState.Programmatic);
        editBox.Select(editBox.Text.Length, 0);
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_activeEditBox == null) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            if (node == _activeEditBox) return;
        EndInlineEdit(save: true);
    }

    private void EndInlineEdit(bool save)
    {
        var vm = _editingItem;
        var editBox = _activeEditBox;
        if (vm == null || editBox == null) return;
        _editingItem = null;
        _activeEditBox = null;
        var value = save ? editBox.Text : vm.Content ?? string.Empty;
        var container = FindCardContainer(vm);
        var text = container == null ? null : FindChildByName<TextBlock>(container, "ContentText");
        editBox.Visibility = Visibility.Collapsed;
        if (text != null)
        {
            text.Text = container is Border row && IsTextListRow(row) ? TextListPreview(value) : value;
            text.Visibility = Visibility.Visible;
        }
        if (container is Border { Child: ContentControl listContent })
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(listContent, TextListPreview(value));
        if (save && (value != vm.Content || _unsavedText.ContainsKey(vm.Id)))
        {
            var oldValue = vm.Content ?? string.Empty;
            vm.Content = value;
            _unsavedText[vm.Id] = value;
            _textSaveTask = SaveTextAfterAsync(_textSaveTask, vm, value, oldValue);
        }
    }

    private async Task SaveTextAfterAsync(Task previous, ItemVM vm, string value, string oldValue)
    {
        await previous;
        try
        {
            await Store.UpdateTextAsync(vm.Id, value);
            if (oldValue != value) RecordDataChange("修改文字", () => Store.UpdateTextAsync(vm.Id, oldValue), () => Store.UpdateTextAsync(vm.Id, value));
            if (_unsavedText.GetValueOrDefault(vm.Id) == value) _unsavedText.Remove(vm.Id);
        }
        catch (Exception error)
        {
            ShowImportError("文字未能保存，请复制内容后重试", error);
            if (_editingItem == null && FindCardContainer(vm) != null)
            {
                StartInlineEdit(vm);
            }
        }
    }

    private void EditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isComposingText) return;
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (!isShift && !isCtrl)
            {
                e.Handled = true;
                EndInlineEdit(save: true);
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            EndInlineEdit(save: false);
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingItem != null)
            EndInlineEdit(save: true);
    }

    // ====================================================================
    //  Window placement persistence (multi-monitor / multi-DPI aware)
    // ====================================================================

    private static readonly string PlacementFile =
        System.IO.Path.Combine(Store.DataDirectory, "window.txt");

    // Last restored-state geometry. Minimized snapshots park the window at
    // -32000, and maximized geometry is not the user's chosen size, so only
    // genuinely restored states are remembered and persisted.
    private PointInt32 _normalPosition;
    private SizeInt32 _normalSize;
    private bool _windowMaximized;
    private bool _pendingMaximize;

    private void RememberPlacement()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized) { _windowMaximized = true; return; }
            if (presenter.State == OverlappedPresenterState.Minimized) return;
            _windowMaximized = false;
        }
        _normalPosition = AppWindow.Position;
        _normalSize = AppWindow.Size;
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var scale = (double)GetDpiForWindow(GetHwnd()) / 96;
            if (scale <= 0) scale = 1;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlacementFile)!);
            System.IO.File.WriteAllText(PlacementFile, string.Join(',',
                _normalPosition.X, _normalPosition.Y, _normalSize.Width, _normalSize.Height,
                (int)Math.Round(scale * 100), _windowMaximized ? 1 : 0));
        }
        catch { /* Silent */ }
    }

    private void RestoreWindowPlacement()
    {
        RectInt32? saved = null;
        var savedScale = 0.0;
        var maximized = false;
        try
        {
            if (System.IO.File.Exists(PlacementFile))
            {
                var parts = System.IO.File.ReadAllText(PlacementFile).Trim().Split(',');
                if (parts.Length >= 4 &&
                    int.TryParse(parts[0], out var sx) && int.TryParse(parts[1], out var sy) &&
                    int.TryParse(parts[2], out var sw) && int.TryParse(parts[3], out var sh) &&
                    // Reject minimized junk (-32000ish) and degenerate sizes.
                    sx > -10000 && sy > -10000 && sw >= 500 && sh >= 350)
                {
                    saved = new RectInt32(sx, sy, sw, sh);
                    if (parts.Length >= 5 && int.TryParse(parts[4], out var pct) && pct is >= 50 and <= 500)
                        savedScale = pct / 100.0;
                    if (parts.Length >= 6 && parts[5] == "1") maximized = true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            saved = null;
        }

        var (display, monitorMatched) = ChooseDisplay(saved);
        var area = display.WorkArea;
        var scale = DisplayScale(display);
        // Files from older versions carry no DPI: assume the saved pixels were
        // taken at the target monitor's scale so the size carries over unchanged.
        if (savedScale <= 0) savedScale = scale;

        int width, height, x, y;
        if (saved is { } s)
        {
            // Normalize by DPI so the perceived size survives monitor moves between sessions.
            width = (int)Math.Clamp(s.Width * scale / savedScale, Math.Min((int)(720 * scale), area.Width), area.Width);
            height = (int)Math.Clamp(s.Height * scale / savedScale, Math.Min((int)(480 * scale), area.Height), area.Height);
            x = monitorMatched ? Math.Clamp(s.X, area.X, area.X + area.Width - width) : area.X + (area.Width - width) / 2;
            y = monitorMatched ? Math.Clamp(s.Y, area.Y, area.Y + area.Height - height) : area.Y + (area.Height - height) / 2;
        }
        else
        {
            // First launch: a comfortable majority of the primary work area, centered.
            width = (int)(area.Width * .72);
            height = (int)(area.Height * .72);
            x = area.X + (area.Width - width) / 2;
            y = area.Y + (area.Height - height) / 2;
        }

        try
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            if (maximized) (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
        }
        catch { /* Silent */ }
        _normalPosition = new PointInt32(x, y);
        _normalSize = new SizeInt32(width, height);
        _windowMaximized = maximized;
        _pendingMaximize = maximized;
    }

    /// <summary>Prefers the display that shows the saved window; nearest viable display otherwise.
    /// Only indexed access is safe: enumerating the projected list with foreach
    /// crashes the process with a WinRT stowed exception in this WinAppSDK version.</summary>
    private static (DisplayArea Display, bool Matched) ChooseDisplay(RectInt32? saved)
    {
        var displays = DisplayArea.FindAll();
        if (displays.Count == 0) return (DisplayArea.Primary, false);
        if (saved is { } s)
        {
            for (var i = 0; i < displays.Count; i++)
            {
                var bounds = displays[i].OuterBounds;
                if (s.X >= bounds.X && s.X < bounds.X + bounds.Width &&
                    s.Y >= bounds.Y && s.Y < bounds.Y + bounds.Height)
                    return (displays[i], true);
            }
            for (var i = 0; i < displays.Count; i++)
            {
                var bounds = displays[i].OuterBounds;
                if (s.X < bounds.X + bounds.Width && s.X + s.Width > bounds.X &&
                    s.Y < bounds.Y + bounds.Height && s.Y + s.Height > bounds.Y)
                    return (displays[i], true);
            }
        }
        var primary = DisplayArea.Primary ?? displays[0];
        return (primary, false);
    }

    private static double DisplayScale(DisplayArea display)
    {
        try
        {
            var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0)
                return dpi / 96.0;
        }
        catch { /* Fall back to 100% */ }
        return 1.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ====================================================================
    //  Audio waveform: draw bars + drag to seek
    // ====================================================================

    private void WaveformCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (canvas.Tag is not ItemVM vm) return;
        // 立即绘制完整波形（progress=0，全部为 unplayed 颜色）
        RedrawWaveform(canvas, vm, 0);
    }

    private void Waveform_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas canvas || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        // 修饰键左键（Ctrl 多选 / Shift 框选）让路给画布级手势。
        if (e.KeyModifiers != Windows.System.VirtualKeyModifiers.None) return;
        e.Handled = true;
        CancelCanvasDrag();
        CancelWaveformSeek();
        if (!canvas.CapturePointer(e.Pointer)) return;
        _seekCanvas = canvas;
        _seekPointerId = e.Pointer.PointerId;
        // 按压波形 = 交互这个音频：接管空格的落点。
        if (canvas.Tag is ItemVM waveVm) _lastPlayable = waveVm;
        SeekToPosition(canvas, e);
    }

    private Canvas? _seekCanvas;
    private uint _seekPointerId;
    private double? _pendingSeekRatio;

    private void ApplyPendingAudioSeek()
    {
        var session = _mediaPlayer.PlaybackSession;
        if (_pendingSeekRatio is not double ratio || session.NaturalDuration.TotalSeconds <= 0 ||
            session.PlaybackState == MediaPlaybackState.Opening) return;
        session.Position = TimeSpan.FromSeconds(ratio * session.NaturalDuration.TotalSeconds);
        _pendingSeekRatio = null;
    }

    private void CancelWaveformSeek()
    {
        var canvas = _seekCanvas;
        _seekCanvas = null;
        canvas?.ReleasePointerCaptures();
    }

    private void Waveform_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _seekCanvas) && e.Pointer.PointerId == _seekPointerId) CancelWaveformSeek();
    }

    private void Waveform_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas canvas || !ReferenceEquals(canvas, _seekCanvas) || e.Pointer.PointerId != _seekPointerId) return;
        e.Handled = true;
        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) { CancelWaveformSeek(); return; }
        SeekToPosition(canvas, e);
    }

    private void Waveform_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas canvas || !ReferenceEquals(canvas, _seekCanvas) || e.Pointer.PointerId != _seekPointerId) return;
        SeekToPosition(canvas, e);
        CancelWaveformSeek();
        e.Handled = true;
    }

    private void SeekToPosition(Canvas canvas, PointerRoutedEventArgs e)
    {
        try
        {
            if (canvas.Tag is not ItemVM vm) return;
            if (string.IsNullOrEmpty(vm.FilePath) || canvas.ActualWidth <= 0) return;

            // Ensure this item is the active player
            if (_playingItem != vm)
            {
                // Start playback for this item
                ToggleMediaPlayback(vm);
            }

            var x = e.GetCurrentPoint(canvas).Position.X;
            var ratio = Math.Clamp(x / canvas.ActualWidth, 0, 1);
            _pendingSeekRatio = ratio;
            ApplyPendingAudioSeek();
            RedrawWaveform(canvas, vm, ratio);
        }
        catch { /* Silent */ }
    }

    private void RedrawWaveform(Canvas canvas, ItemVM vm, double progress)
    {
        var w = canvas.ActualWidth;
        if (w <= 0 && canvas.Parent is FrameworkElement p && p.ActualWidth > 0)
            w = p.ActualWidth;
        if (w <= 0)
            w = 340.0;

        var h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 56.0;

        var barWidth = 3.0;
        var gap = 2.0;
        var totalBars = (int)(w / (barWidth + gap));
        if (totalBars < 1) return;

        var playedColor = (Brush)Application.Current.Resources["AccentBrush"];
        var unplayedColor = ResourceBrush("WaveformBrush");
        var playedBars = (int)(totalBars * Math.Clamp(progress, 0.0, 1.0));

        // 如果已有柱状图数量匹配，复用柱子但按当前宽度重排位置并更新颜色，
        // 避免高频重建引起卡顿或闪烁；位置不重排会让旧宽度布局的柱子溢出画布。
        if (canvas.Children.Count == totalBars)
        {
            for (int i = 0; i < totalBars; i++)
            {
                if (canvas.Children[i] is Microsoft.UI.Xaml.Shapes.Rectangle rect)
                {
                    Canvas.SetLeft(rect, i * (barWidth + gap));
                    var targetBrush = i < playedBars ? playedColor : unplayedColor;
                    if (!ReferenceEquals(rect.Fill, targetBrush))
                    {
                        rect.Fill = targetBrush;
                    }
                }
            }
            return;
        }

        canvas.Children.Clear();
        var rng = new Random(vm.Id.GetHashCode());

        for (int i = 0; i < totalBars; i++)
        {
            var barH = 4 + rng.NextDouble() * (h - 6);
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barH,
                Fill = i < playedBars ? playedColor : unplayedColor,
                RadiusX = 1.5,
                RadiusY = 1.5
            };
            Canvas.SetLeft(rect, i * (barWidth + gap));
            Canvas.SetTop(rect, (h - barH) / 2);
            canvas.Children.Add(rect);
        }
    }

    // ====================================================================
    //  Image full-screen viewer
    // ====================================================================

    private static Brush CategoryHighlightBrush => ResourceBrush("SelectionBrush");
    private static readonly Brush CategoryNormalBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private int _canvasLoadVersion;

    private async Task LoadCanvasAsync()
    {
        var version = ++_canvasLoadVersion;
        // 卡片即将重建：多选是视图级状态，随刷新清空。
        ClearSelection();
        // Finish the initiating input event before detaching controls/native video surfaces.
        await Task.Yield();
        if (_isClosed || version != _canvasLoadVersion) return;
        CancelCanvasDrag();
        CancelWaveformSeek();
        _layoutAnimationTimer?.Stop();
        _activeTransitions.Clear();
        DisposeVideoCards();
        // 上一轮卡片可能悬挂在列表宿主里；本轮整体重载，直接作废旧列表编组。
        ResetListHost();
        CardCanvas.Children.Clear();
        _maxZIndex = 0;

        // 布局预设视图：条目集合来自快照；普通视图走常规筛选。
        _applyingPreset = ActiveLayoutPreset;
        List<Item> rawItems;
        if (_applyingPreset is { } applying)
        {
            var all = await Store.GetItemsAsync(null, null, null, false);
            var ids = applying.Items.Select(i => i.Id).ToHashSet();
            rawItems = all.Where(i => ids.Contains(i.Id)).ToList();
        }
        else
        {
            rawItems = await Store.GetItemsAsync(_selectedCategoryId, _selectedType, _selectedTimeFilter, _favoritesOnly);
        }

        if (version != _canvasLoadVersion) return;

        for (int i = 0; i < rawItems.Count; i++)
        {
            var item = rawItems[i];
            var vm = new ItemVM
            {
                Id = item.Id,
                Type = item.Type,
                Content = _unsavedText.GetValueOrDefault(item.Id, item.Content ?? string.Empty),
                FilePath = item.FilePath,
                FileName = item.FileName,
                CreatedAt = item.CreatedAt,
                IsFavorite = item.IsFavorite,
                CategoryId = item.CategoryId,
                PosX = item.PosX,
                PosY = item.PosY,
                ZIndex = item.ZIndex
            };

            // Load image source for image cards
            if (item.Type == ItemType.Image && !string.IsNullOrEmpty(item.FilePath))
            {
                try
                {
                    var absPath = Store.AbsolutePath(item.FilePath);
                    var bmp = new BitmapImage { DecodePixelWidth = 800 };
                    var file = await StorageFile.GetFileFromPathAsync(absPath);
                    using var stream = await file.OpenAsync(FileAccessMode.Read);
                    await bmp.SetSourceAsync(stream);
                    vm.ImageSource = bmp;
                }
                catch { /* skip image load failure */ }
            }

            if (item.Type == ItemType.Video) await LoadVideoPreviewAsync(vm);
            if (_isClosed || version != _canvasLoadVersion) return;
            var card = CreateCanvasCard(vm);
            // 布局预设视图：按快照恢复坐标 / 层级 / 宽度（含手动调宽），跳过自动平铺。
            if (_applyingPreset is { } preset && preset.Items.FirstOrDefault(i => i.Id == vm.Id) is { } saved)
            {
                card.Width = Math.Clamp(saved.W, 150, 800);
                ApplyCardWidthToContent(card, card.Width);
                Canvas.SetLeft(card, saved.X);
                Canvas.SetTop(card, saved.Y);
                Canvas.SetZIndex(card, saved.Z);
                _maxZIndex = Math.Max(_maxZIndex, saved.Z);
            }
            card.Measure(new Size(card.Width, double.PositiveInfinity));
            CardCanvas.Children.Add(card);
        }

        // Each category/filter is laid out independently; old free-placement coordinates
        // must not leave gaps or push a filtered result out of sight.
        if (_applyingPreset == null)
        {
            if (_isWordCloudMode) ApplyWordCloudPresentation();
            ApplyCompactLayout();
        }
        // 列表模式下卡片随即重新编组挂进列表宿主（画布坐标仍先算好，切回平铺无缝）。
        if (_isListMode) BuildListSections();
        FocusCanvasContent(); // 中心锚点模式下内容不在 (0,0)，取景到内容块左上
    }

    private Border CreateCanvasCard(ItemVM vm)
    {
        var content = BuildCardContent(vm);
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["InspirationCard"],
            // Video cards run wider so the picture, not chrome, owns the space.
            Width = vm.Type == ItemType.Video ? 480 : 360,
            BorderBrush = CardCategoryBrush(vm),
            BorderThickness = new Thickness(vm.CategoryId.HasValue ? 2 : 1),
            DataContext = vm,
            Padding = new Thickness(vm.Type is ItemType.Image or ItemType.Video ? 0 : 20),
            Child = content
        };
        if (vm.Type == ItemType.Text) SizeTextCard(card, content);

        // Card context actions.
        var menu = new MenuFlyout();

        if (vm.Type == ItemType.Text)
        {
            var miEdit = new MenuFlyoutItem { Text = "修改" };
            miEdit.Click += EditTextItem_Click;
            menu.Items.Add(miEdit);
        }

        var miFav = new MenuFlyoutItem { Text = vm.IsFavorite ? "取消收藏" : "收藏", Icon = new FontIcon { Glyph = vm.IsFavorite ? "\uE735" : "\uE734" } };
        miFav.Click += FavoriteItem_Click;
        menu.Items.Add(miFav);

        if (vm.Type == ItemType.Text)
        {
            var miCopy = new MenuFlyoutItem { Text = "复制文本" };
            miCopy.Click += CopyTextItem_Click;
            menu.Items.Add(miCopy);
        }

        var miDel = new MenuFlyoutItem { Text = "删除" };
        miDel.Click += DeleteItem_Click;
        menu.Items.Add(miDel);

        FlyoutBase.SetAttachedFlyout(card, menu);

        // Text card: double-click to start inline edit
        if (vm.Type == ItemType.Text)
        {
            card.DoubleTapped += (_, _) => StartInlineEdit(vm);
        }
        else if (vm.Type == ItemType.Video)
        {
            card.DoubleTapped += (_, e) =>
            {
                if (VideoCardView.IsPlaybackControl(e.OriginalSource)) return;
                ((VideoCardView)card.Child).OpenViewer();
                e.Handled = true;
            };
        }

        // Make draggable
        card.PointerPressed += CanvasCard_PointerPressed;
        card.PointerMoved += CanvasCard_PointerMoved;
        card.PointerReleased += CanvasCard_PointerReleased;
        card.PointerCaptureLost += CanvasCard_PointerCaptureLost;
        card.PointerCanceled += CanvasCard_PointerCaptureLost;
        card.RightTapped += Card_RightTapped;
        card.PointerEntered += Card_PointerEntered;
        card.PointerExited += Card_PointerExited;
        // 视频表面把画面内部的普通左键标记为已处理（点按播放）；低缩放下把手
        // 环宽超过表面 12px 让位环，被吞掉的边缘按下要收听已处理事件才能起手
        // 调整大小（内部命中判定自行放行，非把手区不影响原有点按行为）。
        if (vm.Type == ItemType.Video)
            card.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(CanvasCard_ResizePointerPressed), true);

        // Fade-in on load

        return card;
    }

    /// <summary>
    /// 文本卡初始尺寸按内容长度约束：短文用窄卡（最少 150，上限 360），
    /// 避免"两个字占一张大卡"；所有文本卡结构性最小高度 116 且内容
    /// 垂直居中（便签比例，为小缩放下的正文放大预留真实可用的内容盒
    /// ——放大文字四边边距都真实存在，不再靠字形溢出行盒渲染）。
    /// "水平居中"设置只控制单行文本与日期的水平对齐。
    /// </summary>
    private void SizeTextCard(Border card, FrameworkElement content)
    {
        if (FindChildByName<TextBlock>(card, "ContentText") is not { } text) return;
        var wrapping = text.TextWrapping;

        // 自然宽度（不换行）→ 卡宽 = 自然宽 + 左右内边距 40 + 余量 24。
        text.TextWrapping = TextWrapping.NoWrap;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = text.DesiredSize.Width;
        var width = Math.Clamp(Math.Ceiling(natural) + 64, 150, 360);
        card.Width = width;
        card.MinHeight = 116;
        if (content is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;

        // 换行槽下实测行数（决定水平居中只作用于单行）。
        text.TextWrapping = wrapping;
        text.Measure(new Size(width - 40, double.PositiveInfinity));
        var lines = Math.Max(1, (int)Math.Round(text.DesiredSize.Height / 22.0));

        if (!_textCentered || lines > 1) return;
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        if (FindChildByName<TextBlock>(card, "ContentMeta") is { } meta)
        {
            meta.TextAlignment = TextAlignment.Center;
            meta.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }

    private FrameworkElement BuildCardContent(ItemVM vm, bool textListRow = false)
    {
        var panel = new StackPanel { Spacing = 10 };

        switch (vm.Type)
        {
            case ItemType.Text:
                {
                    var textGrid = new Grid();
                    var fontFamily = new FontFamily(_textFontFamily);

                    var textBlock = new TextBlock
                    {
                        Name = "ContentText",
                        Style = (Style)Application.Current.Resources["CardTextStyle"],
                        Text = vm.Content ?? string.Empty,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = fontFamily,
                        IsTextSelectionEnabled = false
                    };

                    var editBox = new TextBox
                    {
                        Name = "EditBox",
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = fontFamily,
                        Background = ResourceBrush("SidebarBrush"),
                        Foreground = (Brush)Application.Current.Resources["TextBrush"],
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8),
                        FontSize = 15,
                        MinHeight = 88,
                        MaxHeight = 500,
                        Visibility = Visibility.Collapsed
                    };
                    editBox.PointerPressed += (s, e) => e.Handled = true;
                    editBox.KeyDown += EditBox_KeyDown;
                    editBox.LostFocus += EditBox_LostFocus;
                    editBox.TextCompositionStarted += (_, _) => _isComposingText = true;
                    editBox.TextCompositionEnded += (_, _) => _dispatcher.TryEnqueue(() => _isComposingText = false);

                    textGrid.Children.Add(textBlock);
                    textGrid.Children.Add(editBox);
                    if (textListRow) return BuildTextListRow(vm, textGrid);
                    panel.Children.Add(textGrid);
                    break;
                }
            case ItemType.Image:
                {
                    // The rounded shell follows the original image aspect ratio.
                    var source = vm.ImageSource;
                    if (source == null)
                        return new TextBlock { Text = "图片暂时无法读取", Margin = new Thickness(20), Foreground = ResourceBrush("SubTextBrush") };
                    var width = 358.0;
                    var height = source.PixelWidth > 0 ? width * source.PixelHeight / source.PixelWidth : 240;
                    var photo = new Border
                    {
                        Name = "ContentPhoto",
                        // Tag 存纵横比（宽/高），边缘调宽时按比例联动换高。
                        Tag = height > 0 ? width / height : 1.0,
                        Height = height,
                        CornerRadius = new CornerRadius(11),
                        Background = new ImageBrush { ImageSource = source, Stretch = Stretch.Fill }
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(photo, vm.FileName ?? "图片灵感");
                    ToolTipService.SetToolTip(photo, $"{vm.FileName}\n{vm.MetaText}");
                    return photo;
                }
            case ItemType.Audio:
                {
                    panel.Padding = new Thickness(4, 0, 4, 0);
                    var waveBorder = new Border
                    {
                        Background = ResourceBrush("SidebarBrush"),
                        CornerRadius = new CornerRadius(6),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    var waveCanvas = new Canvas
                    {
                        Name = "WaveformCanvas",
                        Height = 56,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Tag = vm
                    };
                    waveCanvas.Loaded += WaveformCanvas_Loaded;
                    // 调宽后柱状图不会自动重排：尺寸变化时按当前进度重画。
                    waveCanvas.SizeChanged += (_, _) => RedrawWaveform(waveCanvas, vm, WaveformProgressFor(vm));
                    waveCanvas.PointerPressed += Waveform_PointerPressed;
                    waveCanvas.PointerMoved += Waveform_PointerMoved;
                    waveCanvas.PointerReleased += Waveform_PointerReleased;
                    waveCanvas.PointerCaptureLost += Waveform_PointerCaptureLost;
                    waveCanvas.PointerCanceled += Waveform_PointerCaptureLost;
                    ToolTipService.SetToolTip(waveCanvas, "拖动波形调整进度；拖动卡片边缘移动音频");
                    waveBorder.Child = waveCanvas;
                    panel.Children.Add(waveBorder);

                    var grid = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                    grid.Tapped += (_, args) => { _lastPlayable = vm; ToggleMediaPlayback(vm); args.Handled = true; };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var playIcon = new FontIcon
                    {
                        FontSize = 14,
                        Foreground = (Brush)Application.Current.Resources["AccentBrush"],
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    playIcon.SetBinding(FontIcon.GlyphProperty, new Microsoft.UI.Xaml.Data.Binding
                    {
                        Source = vm,
                        Path = new PropertyPath(nameof(ItemVM.PlayGlyph)),
                        Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
                    });
                    Grid.SetColumn(playIcon, 0);
                    grid.Children.Add(playIcon);

                    var fileName = new TextBlock
                    {
                        Style = (Style)Application.Current.Resources["CardMetaStyle"],
                        Text = vm.FileNameText,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(fileName, 1);
                    grid.Children.Add(fileName);

                    var meta = new TextBlock
                    {
                        Style = (Style)Application.Current.Resources["CardMetaStyle"],
                        Text = vm.MetaText,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(meta, 2);
                    grid.Children.Add(meta);

                    panel.Children.Add(grid);
                    break;
                }
            case ItemType.Video:
                return BuildVideoCard(vm);
        }

        // Add meta text for text cards
        if (vm.Type == ItemType.Text)
        {
            var meta = new TextBlock
            {
                Name = "ContentMeta",
                Style = (Style)Application.Current.Resources["CardMetaStyle"],
                Text = vm.MetaText
            };
            panel.Children.Add(meta);
        }

        return panel;
    }


    // ====================================================================
    //  Compact tiling
    // ====================================================================

    private void Pack_Click(object sender, RoutedEventArgs e)
    {
        EndInlineEdit(save: true);
        // 紧凑平铺属于画布模式；列表模式下触发时先切回平铺。
        if (_isListMode) SetViewMode("tile");
        if (_isWordCloudMode) ApplyWordCloudPresentation();
        CancelCanvasDrag();
        var cards = CardCanvas.Children.OfType<Border>().ToList();
        if (cards.Count == 0) return;

        var placements = _centeredPacking
            ? BinPackingLayoutEngine.ComputeRadialPacking(cards, CompactLayoutWidth(), gap: 12.0)
            : BinPackingLayoutEngine.ComputeCompactBinPacking(cards, CompactLayoutWidth(), gap: 12.0, originX: 0, originY: 0).ToList();
        double viewX = 0, viewY = 0;
        if (_centeredPacking)
        {
            var (ax, ay) = RadialAnchor(placements);
            (viewX, viewY) = AnchorPlacementsOnViewport(placements, ax, ay); // 最大卡片中心对齐视口中心
        }
        var before = CaptureArrangement();
        var after = before with { Positions = placements.ToDictionary(p => ((ItemVM)p.Card.DataContext).Id, p => (p.TargetX, p.TargetY, p.TargetZIndex)) };
        RecordArrangement(before, after);
        AnimateCardsToPlacements(placements);
        UpdateCanvasExtent();
        // 取景：中心锚点把视口对准最大卡片中心；左上锚点滚到 (0,0)，与旧行为一致。
        if (_centeredPacking) CenterCanvasViewOn(viewX, viewY);
        else CanvasScroll.ChangeView(0, 0, null, disableAnimation: true);
    }

    // ====================================================================
    //  Layout animation: Cubic ease-out transition for card movements
    // ====================================================================

    /// <summary>
    /// 启动动画过渡，将卡片从当前位置平滑移动到算法计算的目标位置。
    /// 排列仅作用于当前视图，避免分类和筛选之间相互覆盖坐标。
    /// </summary>
    private void AnimateCardsToPlacements(List<BinPackingLayoutEngine.PackedPlacement> placements)
    {
        if (placements.Count == 0) return;

        // 停止正在进行的动画
        _layoutAnimationTimer?.Stop();
        _activeTransitions.Clear();

        foreach (var p in placements)
        {
            double startX = Canvas.GetLeft(p.Card);
            double startY = Canvas.GetTop(p.Card);
            _activeTransitions.Add((p.Card, p.Card.DataContext as ItemVM, startX, startY, p.TargetX, p.TargetY));
            Canvas.SetZIndex(p.Card, p.TargetZIndex);
        }
        _maxZIndex = placements.Count;

        _transitionElapsedMs = 0;
        _layoutAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _layoutAnimationTimer.Tick += OnLayoutAnimationTick;
        _layoutAnimationTimer.Start();
    }

    private void OnLayoutAnimationTick(object? sender, object e)
    {
        _transitionElapsedMs += 16.0;
        double t = Math.Clamp(_transitionElapsedMs / TransitionDurationMs, 0.0, 1.0);
        // Cubic ease-out 缓动
        double ease = 1.0 - Math.Pow(1.0 - t, 3.0);

        foreach (var tr in _activeTransitions)
        {
            double curX = tr.StartX + (tr.TargetX - tr.StartX) * ease;
            double curY = tr.StartY + (tr.TargetY - tr.StartY) * ease;
            Canvas.SetLeft(tr.Card, Math.Round(curX));
            Canvas.SetTop(tr.Card, Math.Round(curY));
        }

        if (t >= 1.0)
        {
            _layoutAnimationTimer?.Stop();

            var completed = _activeTransitions.ToArray();
            _activeTransitions.Clear();
            if (completed.Length == 0) return;
            foreach (var tr in completed)
            {
                Canvas.SetLeft(tr.Card, tr.TargetX);
                Canvas.SetTop(tr.Card, tr.TargetY);
            }
            UpdateCanvasExtent();
        }
    }

    // ====================================================================
    //  Canvas: middle-button drag to pan background
    // ====================================================================

    private bool _isPanning;
    private Point _panStart;
    private double _panStartH;
    private double _panStartV;
    // 本次平移会话内已重定基的累计量：move 事件给的是"距按下点的总位移"，
    // 重定基必须只补增量，否则每个事件都按总量重复平移，内容漂移跑赢光标。
    private double _panRebasedX;
    private double _panRebasedY;

    /// <summary>画布平移光标（四向箭头）。Win32 SetCursor 在 WinUI3 被 XAML 框架覆盖，走 ProtectedCursor。</summary>
    private static readonly InputCursor _panCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    private void CanvasScroll_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastMouseCanvasPoint(e);
        // 只处理中键
        if (e.GetCurrentPoint(CanvasScroll).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed)
            return;

        _isPanning = true;
        _panStart = e.GetCurrentPoint(CanvasScroll).Position;
        _panStartH = CanvasScroll.HorizontalOffset;
        _panStartV = CanvasScroll.VerticalOffset;
        _panRebasedX = 0;
        _panRebasedY = 0;
        CanvasScroll.CapturePointer(e.Pointer);
        // 拖动时显示四向箭头光标
        SetWindowCursor(_panCursor);
        e.Handled = true;
    }

    private void CanvasScroll_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 记录画布视口内的最新光标位置：粘贴导入（Ctrl+V）按此落卡。
        UpdateLastMouseCanvasPoint(e);
        // 光标兜底还原：指针不在任何把手/会话/悬停卡上却仍显示调整/平移光标时
        // （卡片收不到退出事件、光标残留），随画布层移动立即还原为箭头。
        if (!_isPanning && _resize == null && _edgeHoverCard == null && _activeCursor != null)
            SetWindowCursor(null);
        if (!_isPanning) return;

        var pos = e.GetCurrentPoint(CanvasScroll).Position;
        var dx = pos.X - _panStart.X;
        var dy = pos.Y - _panStart.Y;

        // 向右拖 → 画布内容跟随鼠标向右移 → offset 减小。
        // 想滚出边界（offset < 0 或超界）时做"重定基"：把所有卡片平移等效量，
        // 视觉零跳变但内容永远有缓冲空间——等效无限画布，不被左上角原点卡住。
        // move 事件按总量计算目标，重定基只补"本次会话未平移过的增量"。
        var desiredH = _panStartH - dx;
        var desiredV = _panStartV - dy;
        var maxH = Math.Max(0, CanvasScroll.ScrollableWidth);
        var maxV = Math.Max(0, CanvasScroll.ScrollableHeight);
        double wantShiftX = 0, wantShiftY = 0;
        if (desiredH < 0) { wantShiftX = -desiredH; desiredH = 0; }
        else if (desiredH > maxH) { wantShiftX = maxH - desiredH; desiredH = maxH; }
        if (desiredV < 0) { wantShiftY = -desiredV; desiredV = 0; }
        else if (desiredV > maxV) { wantShiftY = maxV - desiredV; desiredV = maxV; }
        var deltaX = wantShiftX - _panRebasedX;
        var deltaY = wantShiftY - _panRebasedY;
        if (Math.Abs(deltaX) > 0.01 || Math.Abs(deltaY) > 0.01)
        {
            RebaseCanvasContent(deltaX, deltaY);
            _panRebasedX = wantShiftX;
            _panRebasedY = wantShiftY;
        }

        CanvasScroll.ChangeView((float)desiredH, (float)desiredV, null);
        e.Handled = true;
    }

    private void CanvasScroll_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateLastMouseCanvasPoint(e);

    private void CanvasScroll_PointerExited(object sender, PointerRoutedEventArgs e) =>
        _lastMouseCanvasPoint = null;

    private void UpdateLastMouseCanvasPoint(PointerRoutedEventArgs e)
    {
        try { _lastMouseCanvasPoint = e.GetCurrentPoint(CanvasScroll).Position; }
        catch { /* 坐标取不到时保持上次记录 */ }
    }

    /// <summary>画布重定基：全部卡片坐标平移 shift（正值右/下），滚动区随之等效扩展。</summary>
    private void RebaseCanvasContent(double shiftX, double shiftY)
    {
        if (_isListMode) return;
        foreach (var card in CardCanvas.Children.OfType<Border>().ToList())
        {
            Canvas.SetLeft(card, Canvas.GetLeft(card) + shiftX);
            Canvas.SetTop(card, Canvas.GetTop(card) + shiftY);
        }
        // 进行中的布局过渡目标同步平移，避免动画结束跳回。
        for (var i = 0; i < _activeTransitions.Count; i++)
        {
            var t = _activeTransitions[i];
            _activeTransitions[i] = (t.Card, t.Vm, t.StartX + shiftX, t.StartY + shiftY, t.TargetX + shiftX, t.TargetY + shiftY);
        }
    }

    private void CanvasScroll_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        try { CanvasScroll.ReleasePointerCapture(e.Pointer); } catch { /* Silent */ }
        // 恢复标准箭头光标
        SetWindowCursor(null);
        e.Handled = true;
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private IntPtr GetHwnd()
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(this);
    }
}
