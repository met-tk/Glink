using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Glink.Core;
using Windows.Foundation;

namespace Glink;

public sealed partial class MainWindow
{
    // ====================================================================
    //  侧栏"情绪意向"列表：
    //  · 点击分类行 → 选中该分类；点色块 → 取色 Flyout；双击名称 → 内联重命名
    //  · 分类集合（文件夹）：可折叠/展开，子分类缩进，右键菜单移动
    //  · 按住任意行拖动 → 排序（分类行单独排，集合行整块排）
    //
    //  行由代码构建（不用 ListView：无容器回收/过渡动画的干扰，拖拽与
    //  FLIP 全在掌握中）。行距固定 48，所有位移动画按 索引差×行距 换算。
    //  结构持久化走旁路文件 category-groups.json（Core 无文件夹概念）。
    // ====================================================================

    internal static readonly (string Name, string Hex)[] CategoryPalette =
    {
        ("杏橙", "#E8956B"), ("珊瑚", "#E47878"), ("玫瑰", "#CE83AC"), ("紫藤", "#A794D6"),
        ("雾蓝", "#7CACE2"), ("湖青", "#63B9C3"), ("松绿", "#78AE8D"), ("橄榄", "#B0BB72"),
        ("麦金", "#D7B45F"), ("陶土", "#BC8E72"), ("石灰", "#A6AFB6"), ("深海", "#568BA6"),
        ("焰橙", "#D96F45"), ("樱粉", "#E8A3B8"), ("葡萄", "#8E6FB8"), ("靛蓝", "#6C86C8"),
        ("草木", "#7FAE62"), ("墨玉", "#5A6E6F")
    };

    private const double CategoryRowStride = 48;

    /// <summary>一行 = 一个分类或一个集合头。Root 挂 FLIP 位移，Content 挂缩进/淡入。</summary>
    private sealed class CategoryRowRec
    {
        public required Grid Content;
        public required FrameworkElement Root;
        public CategoryVM? Category;
        public CategoryFolder? Folder;
        public Guid? FolderId;          // 分类行所属集合；集合行为 null
        public bool IsFolder;
        public RotateTransform? Chevron;
        public FrameworkElement? Rail;  // 集合内分类的树形导轨竖线
    }

    private readonly List<CategoryRowRec> _categoryRows = new();
    private CategoryGroupState _groups = CategoryGroupService.Load() ?? new CategoryGroupState();
    private int _categoryAnimVersion;   // 每次 Build/折叠切换自增，使在途延迟收尾作废

    private void SaveGroups() => CategoryGroupService.Save(_groups);

    // ------------------------------------------------------------------
    //  行构建
    // ------------------------------------------------------------------

    private static Guid? ParseId(string token) =>
        token.Length > 2 && Guid.TryParse(token.AsSpan(2), out var id) ? id : null;

    private List<Guid> ChildrenOf(Guid folderId) =>
        _groups.Children.TryGetValue(folderId.ToString(), out var list)
            ? list.Where(id => Guid.TryParse(id, out _)).Select(Guid.Parse).ToList()
            : new List<Guid>();

    /// <summary>结构对齐：清掉已删除的分类/集合引用，补上新建未登记的分类。</summary>
    private void SyncGroupStructure()
    {
        var alive = _categories.Select(c => c.Id).ToHashSet();
        foreach (var key in _groups.Children.Keys.ToList())
            _groups.Children[key].RemoveAll(id => !Guid.TryParse(id, out var g) || !alive.Contains(g));
        _groups.Top.RemoveAll(token =>
        {
            if (token.StartsWith("c:")) return ParseId(token) is not { } id || !alive.Contains(id);
            if (token.StartsWith("f:")) return !_groups.Folders.Any(f => "f:" + f.Id == token);
            return true;
        });
        var known = _groups.Top.Where(t => t.StartsWith("c:")).Select(t => ParseId(t) ?? Guid.Empty).ToHashSet();
        foreach (var id in _groups.Children.Values.SelectMany(v => v))
            if (Guid.TryParse(id, out var g)) known.Add(g);
        foreach (var cat in _categories)
            if (!known.Contains(cat.Id)) _groups.Top.Add("c:" + cat.Id);
    }

    private void BuildCategoryRows()
    {
        _ = ++_categoryAnimVersion;
        SyncGroupStructure();
        _categoryRows.Clear();
        CategoryListHost.Children.Clear();
        foreach (var token in _groups.Top.ToList())
        {
            if (token.StartsWith("f:"))
            {
                var folder = _groups.Folders.FirstOrDefault(f => "f:" + f.Id == token);
                if (folder == null) continue;
                AddCategoryRow(BuildFolderRow(folder));
                if (folder.Collapsed) continue;
                foreach (var childId in ChildrenOf(folder.Id))
                {
                    var vm = _categories.FirstOrDefault(c => c.Id == childId);
                    if (vm != null) AddCategoryRow(BuildCategoryRow(vm, folder.Id));
                }
            }
            else if (token.StartsWith("c:"))
            {
                var vm = _categories.FirstOrDefault(c => c.Id == ParseId(token));
                if (vm != null) AddCategoryRow(BuildCategoryRow(vm, null));
            }
        }
        // 布局预设行跟在分类之后（特殊条目，独立于分类拖拽体系）。
        BuildPresetRows();
    }

    private void AddCategoryRow(CategoryRowRec rec)
    {
        rec.Root.Tag = rec;
        _categoryRows.Add(rec);
        CategoryListHost.Children.Add(rec.Root);
    }

    private CategoryRowRec? RowFromElement(FrameworkElement? element) => element?.Tag as CategoryRowRec;

    private FrameworkElement? CategoryRowElement(CategoryVM vm) =>
        _categoryRows.FirstOrDefault(r => r.Category?.Id == vm.Id)?.Root;

    private CategoryRowRec BuildCategoryRow(CategoryVM vm, Guid? folderId)
    {
        var content = new Grid
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            Background = vm.Background,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 列 0 = 树形导轨（集合内分类的竖线，Auto 列，顶层行折叠为 0 宽）。
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rail = new Border
        {
            Width = 2, Height = 24, CornerRadius = new CornerRadius(1),
            Background = ResourceBrush("BorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 10, 0),
            Visibility = folderId != null ? Visibility.Visible : Visibility.Collapsed
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(5),
            Background = vm.AccentBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = ResourceBrush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "ColorDot"
        };
        dot.Tapped += CategoryColorDot_Tapped;
        ToolTipService.SetToolTip(dot, "点击更改分类颜色");
        AutomationProperties.SetName(dot, "更改分类颜色");

        var name = new TextBlock
        {
            Text = vm.Name, FontSize = 14, FontFamily = new FontFamily("Segoe UI"),
            Foreground = ResourceBrush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        name.DoubleTapped += NameLabel_DoubleTapped;

        var count = new TextBlock
        {
            Text = vm.CountText, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Foreground = ResourceBrush("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center
        };

        content.Children.Add(rail);
        content.Children.Add(dot);
        Grid.SetColumn(dot, 1);
        content.Children.Add(name);
        Grid.SetColumn(name, 2);
        content.Children.Add(count);
        Grid.SetColumn(count, 3);

        // 选中态随 VM 变化即时刷（切分类/切"全部"时 UpdateContentTitle 会改 IsSelected）。
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CategoryVM.Background)) content.Background = vm.Background;
            else if (args.PropertyName == nameof(CategoryVM.AccentBrush)) dot.Background = vm.AccentBrush;
        };

        var root = new Grid { Height = CategoryRowStride, DataContext = vm };
        root.Children.Add(content);
        root.Tapped += CategoryRow_Tapped;
        root.PointerPressed += CategoryRow_PointerPressed;
        root.RightTapped += CategoryRow_RightTapped;
        AutomationProperties.SetName(root, $"分类 {vm.Name}");
        return new CategoryRowRec { Content = content, Root = root, Category = vm, FolderId = folderId, Rail = rail };
    }

    private CategoryRowRec BuildFolderRow(CategoryFolder folder)
    {
        var content = new Grid
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 箭头
        content.ColumnDefinitions.Add(new ColumnDefinition());                            // 名称
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 子数

        var chevron = new FontIcon
        {
            Glyph = "\uE76C", FontSize = 11,
            Foreground = ResourceBrush("SubTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform { Angle = folder.Collapsed ? 0 : 90 },
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var name = new TextBlock
        {
            Text = folder.Name, FontSize = 14, FontFamily = new FontFamily("Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ResourceBrush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        name.DoubleTapped += NameLabel_DoubleTapped;
        var count = new TextBlock
        {
            Text = ChildrenOf(folder.Id).Count.ToString(), FontSize = 11,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = ResourceBrush("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center
        };

        content.Children.Add(chevron);
        content.Children.Add(name);
        Grid.SetColumn(name, 1);
        content.Children.Add(count);
        Grid.SetColumn(count, 2);

        var root = new Grid { Height = CategoryRowStride, DataContext = folder };
        root.Children.Add(content);
        root.Tapped += CategoryRow_Tapped;
        root.PointerPressed += CategoryRow_PointerPressed;
        root.RightTapped += CategoryRow_RightTapped;
        AutomationProperties.SetName(root, $"分类集合 {folder.Name}");
        return new CategoryRowRec
        {
            Content = content, Root = root, Folder = folder, IsFolder = true,
            Chevron = (RotateTransform)chevron.RenderTransform
        };
    }

    // ------------------------------------------------------------------
    //  点击 / 右键菜单
    // ------------------------------------------------------------------

    private void CategoryRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowFromElement(sender as FrameworkElement) is not { } rec) return;
        if (Environment.TickCount64 - _categoryDragEndedTick < 400) return; // 拖拽释放的那次点击不算
        if (rec.IsFolder) { ToggleFolder(rec.Folder!); return; }
        _favoritesOnly = false;
        _selectedCategoryId = rec.Category!.Id;
        _selectedPresetId = null;
        AllHighlight.Visibility = Visibility.Collapsed;
        _ = RefreshItemsAsync();
    }

    private void CategoryRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RowFromElement(sender as FrameworkElement) is not { } rec) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            return item;
        }

        if (rec.IsFolder)
        {
            var folder = rec.Folder!;
            menu.Items.Add(Item(folder.Collapsed ? "展开集合" : "折叠集合", () => ToggleFolder(folder)));
            menu.Items.Add(Item("重命名", () =>
                ShowCategoryEditFlyout(rec.Root, "重命名集合", "保存", folder.Name, null,
                    (name, _) => { folder.Name = name; SaveGroups(); BuildCategoryRows(); return Task.CompletedTask; })));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("删除集合", () => DeleteFolder(folder)));
        }
        else
        {
            var vm = rec.Category!;
            menu.Items.Add(Item("重命名", () =>
                ShowCategoryEditFlyout(rec.Root, "重命名分类", "保存", vm.Name, null,
                    (name, _) => CommitCategoryRenameAsync(vm, name))));
            if (_groups.Folders.Count > 0 || rec.FolderId != null)
            {
                var sub = new MenuFlyoutSubItem { Text = "移动到" };
                foreach (var folder in _groups.Folders.ToList())
                {
                    if (folder.Id == rec.FolderId) continue;
                    var captured = folder;
                    sub.Items.Add(Item(folder.Name, () => MoveCategoryToFolder(vm, captured)));
                }
                if (rec.FolderId != null)
                    sub.Items.Add(Item("移出到顶层", () => MoveCategoryToFolder(vm, null)));
                if (sub.Items.Count > 0) menu.Items.Add(sub);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("删除分类", () => _ = DeleteCategoryAsync(vm)));
        }
        menu.ShowAt(rec.Root, new FlyoutShowOptions { Position = e.GetPosition(rec.Root) });
    }

    internal void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCategoryEditFlyout(NewFolderButton, "新建分类集合", "创建", string.Empty, null,
            (name, _) =>
            {
                var folder = new CategoryFolder { Name = name };
                _groups.Folders.Add(folder);
                _groups.Top.Add("f:" + folder.Id);
                SaveGroups();
                BuildCategoryRows();
                return Task.CompletedTask;
            });
    }

    /// <summary>删除集合：子分类原地退回顶层（分类本身不动）。</summary>
    private void DeleteFolder(CategoryFolder folder)
    {
        _groups.Folders.Remove(folder);
        _groups.Children.Remove(folder.Id.ToString());
        _categoryRows.RemoveAll(r => r.IsFolder && r.Folder!.Id == folder.Id);
        foreach (var row in _categoryRows.Where(r => r.FolderId == folder.Id)) row.FolderId = null;
        PersistStructureFromRows();
        SaveGroups();
        BuildCategoryRows();
    }

    /// <summary>菜单移动分类进/出集合：重排 + 缩进平移动画。</summary>
    private void MoveCategoryToFolder(CategoryVM vm, CategoryFolder? folder)
    {
        var rec = _categoryRows.FirstOrDefault(r => r.Category?.Id == vm.Id);
        if (rec == null) return;
        ReorderRowsWithFlip(() =>
        {
            _categoryRows.Remove(rec);
            var target = _categoryRows.Count;
            if (folder != null)
            {
                var header = _categoryRows.FindIndex(r => r.IsFolder && r.Folder!.Id == folder.Id);
                if (header >= 0)
                {
                    target = header + 1;
                    while (target < _categoryRows.Count && !_categoryRows[target].IsFolder
                           && _categoryRows[target].FolderId == folder.Id)
                        target++;
                }
            }
            _categoryRows.Insert(Math.Clamp(target, 0, _categoryRows.Count), rec);
        }, rec);
        rec.FolderId = folder?.Id;
        ApplyRowIndentAnimated(rec);
        PersistStructureFromRows();
        SaveGroups();
    }

    internal async Task DeleteCategoryAsync(CategoryVM vm)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "删除分类",
            Content = new TextBlock { Text = $"确定要删除「{vm.Name}」吗？该分类下的条目将变为未分类。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Background = (Brush)Application.Current.Resources["CardBrush"],
            Foreground = (Brush)Application.Current.Resources["TextBrush"]
        };
        if (await ThemeService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var category = (await Store.GetCategoriesAsync()).Single(c => c.Id == vm.Id);
            var itemIds = (await Store.GetItemsAsync(vm.Id, null, null, false)).Select(i => i.Id).ToArray();
            await Store.DeleteCategoryAsync(vm.Id);
            RecordDataChange("删除分类", () => Store.RestoreCategoryAsync(category, itemIds), () => Store.DeleteCategoryAsync(vm.Id));
            if (_selectedCategoryId == vm.Id) _selectedCategoryId = null;
            await RefreshCategoriesAsync();
            await RefreshItemsAsync();
        }
        catch (Exception error) { ShowImportError("未能删除分类", error); }
    }

    // ------------------------------------------------------------------
    //  集合折叠 / 展开（动效：子行淡出左滑 → 移除后下方行 FLIP 上移；
    //  展开反向。连续快速切换由 _categoryAnimVersion 使旧收尾作废。）
    // ------------------------------------------------------------------

    private void ToggleFolder(CategoryFolder folder)
    {
        folder.Collapsed = !folder.Collapsed;
        _ = ++_categoryAnimVersion;
        var headerIndex = _categoryRows.FindIndex(r => r.IsFolder && r.Folder!.Id == folder.Id);
        if (headerIndex < 0) { SaveGroups(); BuildCategoryRows(); return; }
        var rec = _categoryRows[headerIndex];
        AnimateChevron(rec.Chevron, folder.Collapsed ? 0 : 90);
        SaveGroups();

        if (folder.Collapsed)
        {
            var children = new List<CategoryRowRec>();
            for (var i = headerIndex + 1;
                 i < _categoryRows.Count && !_categoryRows[i].IsFolder && _categoryRows[i].FolderId == folder.Id;
                 i++)
                children.Add(_categoryRows[i]);
            if (children.Count == 0) return;
            foreach (var child in children) FadeRowOut(child.Content);
            Delay(140, () =>
            {
                foreach (var child in children)
                {
                    _categoryRows.Remove(child);
                    CategoryListHost.Children.Remove(child.Root);
                }
                FlipRowsFrom(headerIndex + 1, children.Count * CategoryRowStride);
            });
        }
        else
        {
            var existing = _categoryRows.Skip(headerIndex + 1)
                .TakeWhile(r => !r.IsFolder && r.FolderId == folder.Id).ToList();
            if (existing.Count > 0)
            {
                // 折叠的移除还在途：直接反向淡入即可。
                foreach (var row in existing) FadeRowIn(row.Content);
                return;
            }
            var inserted = new List<CategoryRowRec>();
            var insertAt = headerIndex + 1;
            foreach (var childId in ChildrenOf(folder.Id))
            {
                var vm = _categories.FirstOrDefault(c => c.Id == childId);
                if (vm == null) continue;
                var row = BuildCategoryRow(vm, folder.Id);
                row.Content.Opacity = 0;
                inserted.Add(row);
            }
            if (inserted.Count == 0) return;
            foreach (var row in inserted) row.Root.Tag = row;
            _categoryRows.InsertRange(insertAt, inserted);
            SyncHostOrder(); // 自愈式同步：插入/移动全走安全索引，不再手工算位置
            FlipRowsFrom(insertAt + inserted.Count, -inserted.Count * CategoryRowStride);
            foreach (var row in inserted) FadeRowIn(row.Content);
        }
    }

    /// <summary>延迟收尾（折叠移除等）：期间发生新的结构变化则作废。</summary>
    private async void Delay(int ms, Action action)
    {
        var version = _categoryAnimVersion;
        await Task.Delay(ms);
        if (_isClosed || version != _categoryAnimVersion) return;
        action();
    }

    private static void AnimateChevron(RotateTransform? rotate, double to)
    {
        if (rotate == null) return;
        var story = new Storyboard();
        story.Children.Add(DoubleAnimOn(rotate, "Angle", rotate.Angle, to, 150));
        story.Begin();
    }

    private static void FadeRowOut(FrameworkElement content)
    {
        var slide = new TranslateTransform();
        content.RenderTransform = slide;
        var story = new Storyboard();
        story.Children.Add(DoubleAnimOn(content, "Opacity", 1, 0, 140));
        story.Children.Add(DoubleAnimOn(slide, "X", 0, -12, 140));
        story.Begin();
    }

    private static void FadeRowIn(FrameworkElement content)
    {
        var slide = new TranslateTransform { X = -12 };
        content.RenderTransform = slide;
        var story = new Storyboard();
        story.Children.Add(DoubleAnimOn(content, "Opacity", 0, 1, 170));
        story.Children.Add(DoubleAnimOn(slide, "X", -12, 0, 170));
        story.Begin();
    }

    // ------------------------------------------------------------------
    //  结构落盘：行序即结构（"前一行是集合头/子行 → 归属该集合"）
    // ------------------------------------------------------------------

    private void PersistStructureFromRows()
    {
        // 行序即结构：可见行重建 Top/Children；折叠集合的子行不可见，
        // 先快照再合并，避免把"收起来了"误当成"没有孩子"。
        var previousChildren = _groups.Children;
        _groups.Top.Clear();
        _groups.Children = new Dictionary<string, List<string>>();
        foreach (var rec in _categoryRows)
        {
            if (rec.IsFolder)
            {
                _groups.Top.Add("f:" + rec.Folder!.Id);
                _groups.Children[rec.Folder.Id.ToString()] = new List<string>();
            }
            else if (rec.FolderId is { } folderId)
            {
                var key = folderId.ToString();
                if (!_groups.Children.TryGetValue(key, out var kids))
                    _groups.Children[key] = kids = new List<string>();
                kids.Add(rec.Category!.Id.ToString());
            }
            else _groups.Top.Add("c:" + rec.Category!.Id);
        }
        foreach (var folder in _groups.Folders)
        {
            if (!folder.Collapsed) continue;
            var key = folder.Id.ToString();
            _groups.Children.TryGetValue(key, out var kids);
            kids ??= new List<string>();
            if (previousChildren.TryGetValue(key, out var snapshot))
                foreach (var id in snapshot)
                    if (!kids.Contains(id)) kids.Add(id);
            _groups.Children[key] = kids;
        }
    }

    /// <summary>向上找最近的集合头（越过同集合的子行；遇到顶层分类即无主）。</summary>
    private CategoryRowRec? OwnerFolderRowFor(CategoryRowRec rec)
    {
        for (var i = _categoryRows.IndexOf(rec) - 1; i >= 0; i--)
        {
            if (_categoryRows[i].IsFolder) return _categoryRows[i];
            if (_categoryRows[i].FolderId == null) return null;
        }
        return null;
    }

    /// <summary>拖拽落位时按前一行推导归属：紧随集合头/子行之后 = 入集合。</summary>
    private void ApplyMembershipByPredecessor(CategoryRowRec rec)
    {
        var owner = OwnerFolderRowFor(rec)?.Folder?.Id;
        if (rec.FolderId == owner) return;
        rec.FolderId = owner;
        ApplyRowIndent(rec); // 行隐藏中，直接改缩进，落位显示时即正确
    }

    private void ApplyRowIndent(CategoryRowRec rec)
    {
        if (rec.Rail == null) return;
        rec.Rail.Visibility = rec.FolderId != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>可见行的缩进变化：导轨显隐 + 从旧缩进宽度平移滑入。</summary>
    private void ApplyRowIndentAnimated(CategoryRowRec rec)
    {
        if (rec.Rail == null) return;
        var wasIndented = rec.Rail.Visibility == Visibility.Visible;
        ApplyRowIndent(rec);
        if (wasIndented == (rec.Rail.Visibility == Visibility.Visible)) return;
        var slide = new TranslateTransform { X = wasIndented ? 18 : -18 }; // 18 = 导轨列总宽
        rec.Content.RenderTransform = slide;
        var story = new Storyboard();
        story.Children.Add(DoubleAnimOn(slide, "X", wasIndented ? 18 : -18, 0, 170));
        story.Begin();
    }

    // ------------------------------------------------------------------
    //  颜色：点击色块弹出圆形色板
    // ------------------------------------------------------------------

    private void CategoryColorDot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryVM vm } dot) return;
        // 吃掉事件，避免同一次点击又触发行选中。
        e.Handled = true;
        ShowCategoryColorFlyout(vm, dot);
    }

    private Style? _flyoutCardStyle;
    private Style FlyoutCardStyle => _flyoutCardStyle ??= new Style(typeof(FlyoutPresenter))
    {
        Setters =
        {
            new Setter(FlyoutPresenter.BackgroundProperty, ResourceBrush("CardBrush")),
            new Setter(FlyoutPresenter.BorderBrushProperty, ResourceBrush("BorderBrush")),
            new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)),
            new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(12)),
            new Setter(FlyoutPresenter.PaddingProperty, new Thickness(16))
        }
    };

    private void ShowCategoryColorFlyout(CategoryVM category, FrameworkElement target)
    {
        var flyout = new Flyout { FlyoutPresenterStyle = FlyoutCardStyle, Placement = FlyoutPlacementMode.Bottom };
        // 六列三行的 18 色板；行数必须与色数匹配，多余的空行会因 RowSpacing 留出空白带。
        var board = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        for (var i = 0; i < 6; i++) board.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < (CategoryPalette.Length + 5) / 6; i++) board.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < CategoryPalette.Length; i++)
        {
            var (name, hex) = CategoryPalette[i];
            var swatch = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(CategoryVM.HexToColor(hex))
            };
            FrameworkElement cell = swatch;
            // 当前颜色套一圈细环，其余颜色悬停放大提示可选。
            if (string.Equals(category.AccentHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                cell = new Border
                {
                    BorderThickness = new Thickness(2),
                    BorderBrush = ResourceBrush("TextBrush"),
                    CornerRadius = new CornerRadius(15),
                    Padding = new Thickness(2),
                    Child = swatch
                };
            }
            else
            {
                AttachHoverScale(swatch, 1.18);
            }
            ToolTipService.SetToolTip(cell, name);
            AutomationProperties.SetName(cell, name + "颜色");
            var chosen = hex;
            cell.Tapped += async (_, args) =>
            {
                args.Handled = true;
                flyout.Hide();
                try { await ChangeCategoryColorAsync(category, chosen); }
                catch (Exception error) { ShowImportError("未能更改分类颜色", error); }
            };
            Grid.SetColumn(cell, i % 6);
            Grid.SetRow(cell, i / 6);
            board.Children.Add(cell);
        }

        flyout.Content = new StackPanel { Spacing = 12, Children = { board } };
        flyout.ShowAt(target);
    }

    private static void AttachHoverScale(FrameworkElement element, double hoverScale)
    {
        var scale = new ScaleTransform();
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.PointerEntered += (_, _) => AnimateScaleTo(scale, hoverScale);
        element.PointerExited += (_, _) => AnimateScaleTo(scale, 1.0);
    }

    private static void AnimateScaleTo(ScaleTransform scale, double to)
    {
        var story = new Storyboard();
        foreach (var path in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, scale);
            Storyboard.SetTargetProperty(anim, path);
            story.Children.Add(anim);
        }
        story.Begin();
    }

    // ------------------------------------------------------------------
    //  新建 / 重命名：浮动卡片（同色板语言），新建时附 18 色选择
    // ------------------------------------------------------------------

    private void ShowCategoryEditFlyout(FrameworkElement anchor, string title, string confirmText,
        string initialName, string? initialColor, Func<string, string, Task> onConfirm)
    {
        var flyout = new Flyout { FlyoutPresenterStyle = FlyoutCardStyle, Placement = FlyoutPlacementMode.Right };

        var nameBox = new TextBox
        {
            Text = initialName,
            PlaceholderText = "名称",
            MaxLength = 40,
            BorderThickness = new Thickness(1),
            BorderBrush = ResourceBrush("BorderBrush"),
            Background = ResourceBrush("SidebarBrush"),
            Foreground = ResourceBrush("TextBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9)
        };
        var composing = false;
        nameBox.TextCompositionStarted += (_, _) => composing = true;
        nameBox.TextCompositionEnded += (_, _) => _ = DispatcherQueue.TryEnqueue(() => composing = false);

        var content = new StackPanel { Width = 300, Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = title, FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ResourceBrush("TextBrush")
        });
        content.Children.Add(nameBox);

        var selectedColor = initialColor;
        if (initialColor != null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "颜色", FontSize = 12, Foreground = ResourceBrush("SubTextBrush"),
                Margin = new Thickness(0, -4, 0, -6)
            });
            // 六列等分铺满弹层宽度，色块较取色板略大，右侧不再留空。
            var board = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (var i = 0; i < 6; i++) board.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < (CategoryPalette.Length + 5) / 6; i++) board.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = new List<(string Hex, Border Ring)>();
            for (var i = 0; i < CategoryPalette.Length; i++)
            {
                var (name, hex) = CategoryPalette[i];
                var swatch = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                    Background = new SolidColorBrush(CategoryVM.HexToColor(hex))
                };
                var ring = new Border
                {
                    CornerRadius = new CornerRadius(17), Padding = new Thickness(2), Child = swatch,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    BorderThickness = new Thickness(string.Equals(initialColor, hex, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                    BorderBrush = ResourceBrush("TextBrush")
                };
                AttachHoverScale(swatch, 1.18);
                ToolTipService.SetToolTip(ring, name);
                AutomationProperties.SetName(ring, name + "颜色");
                ring.Tapped += (_, args) =>
                {
                    args.Handled = true;
                    selectedColor = hex;
                    foreach (var (cellHex, cellRing) in cells)
                        cellRing.BorderThickness = new Thickness(string.Equals(cellHex, hex, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
                };
                cells.Add((hex, ring));
                Grid.SetColumn(ring, i % 6);
                Grid.SetRow(ring, i / 6);
                board.Children.Add(ring);
            }
            content.Children.Add(board);
        }

        var confirm = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = confirmText, CornerRadius = new CornerRadius(8),
            IsEnabled = !string.IsNullOrWhiteSpace(initialName)
        };
        var cancel = new Button
        {
            Content = "取消", CornerRadius = new CornerRadius(8),
            Background = ResourceBrush("TransparentBrush"),
            BorderBrush = ResourceBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Foreground = ResourceBrush("TextBrush"),
            Padding = new Thickness(16, 9, 16, 9)
        };
        cancel.Click += (_, _) => flyout.Hide();
        nameBox.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        nameBox.KeyDown += (_, args) =>
        {
            if (composing) return;
            if (args.Key == Windows.System.VirtualKey.Enter && confirm.IsEnabled)
            {
                args.Handled = true;
                flyout.Hide();
                _ = RunCategoryConfirm(onConfirm, nameBox.Text.Trim(), selectedColor);
            }
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            _ = RunCategoryConfirm(onConfirm, nameBox.Text.Trim(), selectedColor);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);

        flyout.Content = content;
        flyout.ShowAt(anchor);
        nameBox.Focus(FocusState.Programmatic);
        nameBox.Select(nameBox.Text.Length, 0);
    }

    private async Task RunCategoryConfirm(Func<string, string, Task> onConfirm, string name, string? color)
    {
        try { await onConfirm(name, color ?? AccentPalette[0]); }
        catch (Exception error) { ShowImportError("未能保存分类", error); }
    }

    // ------------------------------------------------------------------
    //  重命名：双击名称原地编辑（分类与集合共用）
    // ------------------------------------------------------------------

    private (TextBlock Label, TextBox Box, Action Commit)? _categoryEdit;

    private void NameLabel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not TextBlock label) return;
        e.Handled = true;
        if (_categoryEdit != null) CommitCategoryEdit(save: true);
        if (label.Parent is not Grid host) return;
        var current = label.DataContext switch
        {
            CategoryVM vm => vm.Name,
            CategoryFolder folder => folder.Name,
            _ => null
        };
        if (current == null) return;

        label.Visibility = Visibility.Collapsed;
        bool composing = false;
        var box = new TextBox
        {
            Text = current,
            FontSize = 13,
            MaxLength = 40,
            PlaceholderText = "名称",
            VerticalAlignment = VerticalAlignment.Center
        };
        // 与卡片编辑框一致的 IME 保护：组词中的 Enter 不当作提交。
        box.TextCompositionStarted += (_, _) => composing = true;
        box.TextCompositionEnded += (_, _) => _ = DispatcherQueue.TryEnqueue(() => composing = false);
        box.KeyDown += (_, args) =>
        {
            if (composing) return;
            if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; CommitCategoryEdit(save: true); }
            else if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; CommitCategoryEdit(save: false); }
        };
        box.LostFocus += (_, _) => CommitCategoryEdit(save: true);

        Action commit = label.DataContext switch
        {
            CategoryVM vm => () =>
            {
                var name = box.Text.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name != vm.Name)
                    _ = CommitCategoryRenameAsync(vm, name);
            },
            CategoryFolder folder => () =>
            {
                var name = box.Text.Trim();
                if (string.IsNullOrWhiteSpace(name) || name == folder.Name) return;
                folder.Name = name;
                label.Text = name;
                SaveGroups();
            },
            _ => () => { }
        };

        Grid.SetColumn(box, Grid.GetColumn(label));
        host.Children.Add(box);
        _categoryEdit = (label, box, commit);
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    private void CommitCategoryEdit(bool save)
    {
        if (_categoryEdit is not { } edit) return;
        _categoryEdit = null;
        var (label, box, commit) = edit;
        if (box.Parent is Panel panel) panel.Children.Remove(box);
        label.Visibility = Visibility.Visible;
        if (save) commit();
    }

    private async Task CommitCategoryRenameAsync(CategoryVM vm, string newName)
    {
        try
        {
            var oldName = vm.Name;
            await Store.RenameCategoryAsync(vm.Id, newName);
            if (oldName != newName) RecordDataChange("分类名称", () => Store.RenameCategoryAsync(vm.Id, oldName), () => Store.RenameCategoryAsync(vm.Id, newName));
            await RefreshCategoriesAsync();
            UpdateContentTitle();
        }
        catch (Exception error)
        {
            ShowImportError("未能重命名分类", error);
        }
    }

    // ------------------------------------------------------------------
    //  拖拽排序：16ms 定时器轮询驱动（行按住后无指针捕获问题，但轮询机制
    //  沿用已验证的实现）。位移阈值 8px 保证点击/双击/取色不受影响。
    //
    //  视觉（dnd-kit DragOverlay 一派的做法）：
    //  · 拿起 —— 幽灵是被拖整行的复制品，ThemeShadow 投影 + z 抬升 +
    //    轻微放大倾斜，保留抓取点偏移（拿到哪儿就跟到哪儿）；
    //  · 让位 —— 其余行做 FLIP 滑移（行距一致，按索引差算位移），
    //    空出的间隙本身就是落点指示；
    //  · 落位 —— 松手时幽灵飞回最终槽位并淡出。
    //  分类行单独移动；集合行整块移动（块内子行跟随）。
    // ------------------------------------------------------------------

    private bool _categoryDragActive;
    private CategoryRowRec? _categoryDragRec;
    private Border? _categoryDragGhost;
    private ScaleTransform? _categoryDragGhostScale;
    private double _categoryDragStartY;
    private double _categoryDragGrabDX;
    private double _categoryDragGrabDY;
    private bool _categoryDragMoving;
    private long _categoryDragEndedTick;
    private DispatcherQueueTimer? _categoryDragTimer;
    private readonly Dictionary<FrameworkElement, Storyboard> _categoryRowFlips = new();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    private const int SW_MAXIMIZE = 3;

    private const int VK_LBUTTON = 0x01;

    private void CategoryRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (RowFromElement(sender as FrameworkElement) is not { } rec) return;
        if (_categoryEdit != null) return; // 内联重命名中按住文本框 = 选词，不进拖拽
        if (!e.GetCurrentPoint(rec.Root).Properties.IsLeftButtonPressed) return;
        // 色块点击用于取色，不进入拖拽。
        for (var node = e.OriginalSource as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Tag: "ColorDot" }) return;
        if (!rec.IsFolder && _categories.All(c => c.Id != rec.Category!.Id)) return;
        _categoryDragRec = rec;
        _categoryDragMoving = false;
        _categoryDragStartY = CursorRootPoint().Y;
        _categoryDragTimer ??= DispatcherQueue.CreateTimer();
        _categoryDragTimer.Interval = TimeSpan.FromMilliseconds(16);
        _categoryDragTimer.Tick -= CategoryDragTimer_Tick;
        _categoryDragTimer.Tick += CategoryDragTimer_Tick;
        _categoryDragTimer.Start();
    }

    /// <summary>当前光标在 RootGrid 坐标系里的位置（DIP）。窗口顶部即 RootGrid 顶部。</summary>
    private (double X, double Y) CursorRootPoint()
    {
        if (!GetCursorPos(out var point)) return (double.NaN, double.NaN);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetWindowRect(hwnd, out var rect)) return (double.NaN, double.NaN);
        var scale = Content.XamlRoot?.RasterizationScale ?? 1;
        return scale > 0 ? ((point.X - rect.Left) / scale, (point.Y - rect.Top) / scale) : (double.NaN, double.NaN);
    }

    private void CategoryDragTimer_Tick(object? sender, object e)
    {
        var rec = _categoryDragRec;
        if (rec == null || !_categoryRows.Contains(rec)) { StopCategoryDragTimer(); EndCategoryDrag(); return; }
        try
        {
            TickCategoryDrag();
        }
        catch
        {
            // 拖拽中的任何异常都降级为"结束本次拖拽"，不让它升级成进程崩溃。
            EndCategoryDrag();
        }
    }

    private void TickCategoryDrag()
    {
        var rec = _categoryDragRec!;
        // 左键已松开：结束拖拽（真实拖拽过则抑制紧随的点击并落盘）。
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
        {
            var moved = _categoryDragMoving;
            EndCategoryDrag();
            if (moved)
            {
                _categoryDragEndedTick = Environment.TickCount64;
                PersistStructureFromRows();
                SaveGroups();
            }
            return;
        }
        var (rootX, rootY) = CursorRootPoint();
        if (double.IsNaN(rootY)) return;
        var delta = rootY - _categoryDragStartY;
        if (!_categoryDragMoving)
        {
            if (Math.Abs(delta) < 8) return; // 位移阈值，先让点击/双击判定
            _categoryDragMoving = true;
            _categoryDragActive = true;
            BeginCategoryDragVisuals(rootX, rootY);
        }
        PositionCategoryDragGhost(rootX, rootY);
        var target = CategoryDropIndex(rootY);
        if (rec.IsFolder) MoveFolderBlockWithFlip(target);
        else if (CategoryCursorInRootZone(rootY))
        {
            // 列表末行下方的空白区 = 顶层放置区（文件树侧栏的通用语义）：
            // 从集合里拖出的行落到顶层末尾；否则当列表以集合子行结尾时，
            // 底部永远取不到顶层槽位（前驱归集），拖出无处可放。
            SetDragJoinHighlight(null);
            MoveCategoryRowToRootEnd(rec);
        }
        else
        {
            MoveCategoryRowWithFlip(target);
            SetDragJoinHighlight(rec); // 落点将落入集合时高亮该集合头，给"装进去"的预期
        }
        UpdateCategoryDragHiddenRows();
    }

    /// <summary>光标是否在列表末行下方的空白放置区（= 顶层落点，滚动区范围内）。</summary>
    private bool CategoryCursorInRootZone(double rootY)
    {
        if (CategoryListHost.Parent is not FrameworkElement viewport) return false;
        var hostTop = CategoryListHost.TransformToVisual(RootGrid).TransformPoint(new Point()).Y;
        if (rootY < hostTop + _categoryRows.Count * CategoryRowStride + 6) return false;
        var bounds = viewport.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        return rootY <= bounds.Bottom;
    }

    /// <summary>顶层末尾落位：行移动到列表末尾并脱离集合（缩进导轨即刻回收）。</summary>
    private void MoveCategoryRowToRootEnd(CategoryRowRec rec)
    {
        if (_categoryRows.IndexOf(rec) != _categoryRows.Count - 1)
            ReorderRowsWithFlip(() =>
            {
                _categoryRows.Remove(rec);
                _categoryRows.Add(rec);
            }, rec);
        if (rec.FolderId != null)
        {
            rec.FolderId = null;
            ApplyRowIndent(rec);
        }
    }

    private CategoryRowRec? _categoryDragJoinHighlight;

    /// <summary>分类拖拽的入集指示：落点归属哪个集合，就把那个集合头点亮。</summary>
    private void SetDragJoinHighlight(CategoryRowRec? draggingRow)
    {
        var target = draggingRow == null ? null : OwnerFolderRowFor(draggingRow);
        if (ReferenceEquals(_categoryDragJoinHighlight, target)) return;
        if (_categoryDragJoinHighlight != null) _categoryDragJoinHighlight.Content.Background = null;
        _categoryDragJoinHighlight = target;
        if (target != null) target.Content.Background = ResourceBrush("SelectionBrush");
    }

    /// <summary>跨过位移阈值的那一刻：量好几何、建幽灵、隐掉被拖行。</summary>
    private void BeginCategoryDragVisuals(double rootX, double rootY)
    {
        var rec = _categoryDragRec!;
        var width = Math.Max(CategoryListHost.ActualWidth, 160);
        var point = rec.Root.TransformToVisual(RootGrid).TransformPoint(new Point());
        // 抓取点偏移：幽灵贴着光标相对被拖行的原始位置跟随（dnd-kit overlay 语义）。
        _categoryDragGrabDX = rootX - point.X;
        _categoryDragGrabDY = rootY - point.Y;
        ShowCategoryDragGhost(rec, width, rootX, rootY);
        UpdateCategoryDragHiddenRows();
    }

    /// <summary>被拖行的整行复制品：卡片底 + 投影 + 轻微放大倾斜，弹起入场。</summary>
    private void ShowCategoryDragGhost(CategoryRowRec rec, double width, double rootX, double rootY)
    {
        FrameworkElement icon;
        var nameText = rec.IsFolder ? rec.Folder!.Name : rec.Category!.Name;
        var countText = rec.IsFolder ? ChildrenOf(rec.Folder!.Id).Count.ToString() : rec.Category!.CountText;
        if (rec.IsFolder)
        {
            icon = new FontIcon
            {
                Glyph = "\uE76C", FontSize = 11,
                Foreground = ResourceBrush("SubTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform { Angle = 90 },
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
        }
        else
        {
            icon = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(5),
                Background = rec.Category!.AccentBrush,
                BorderThickness = new Thickness(1),
                BorderBrush = ResourceBrush("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        var name = new TextBlock
        {
            Text = nameText, FontSize = 14, FontFamily = new FontFamily("Segoe UI"),
            FontWeight = rec.IsFolder ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = ResourceBrush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var count = new TextBlock
        {
            Text = countText, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Foreground = ResourceBrush("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        row.Children.Add(name);
        Grid.SetColumn(name, 1);
        row.Children.Add(count);
        Grid.SetColumn(count, 2);

        _categoryDragGhostScale = new ScaleTransform();
        _categoryDragGhost = new Border
        {
            Width = width, Height = CategoryRowStride,
            Background = ResourceBrush("CardBrush"),
            BorderBrush = ResourceBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            Opacity = 0.97,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 48), // z 抬升让投影落得开
            RenderTransform = new TransformGroup
            {
                Children = { _categoryDragGhostScale, new RotateTransform { Angle = 1.2 } }
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = row,
            IsHitTestVisible = false
        };
        DragLayer.Children.Add(_categoryDragGhost);
        PositionCategoryDragGhost(rootX, rootY);

        // 弹起：0.92 → 1.045 快速放大 + 淡入，模拟"被指尖捏起"。
        var pop = new Storyboard();
        foreach (var path in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimation
            {
                From = 0.92, To = 1.045,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, _categoryDragGhostScale);
            Storyboard.SetTargetProperty(anim, path);
            pop.Children.Add(anim);
        }
        pop.Children.Add(DoubleAnimOn(_categoryDragGhost, "Opacity", 0, 0.97, 150));
        pop.Begin();
    }

    private static DoubleAnimation DoubleAnimOn(DependencyObject target, string property, double from, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = from, To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    private void PositionCategoryDragGhost(double rootX, double rootY)
    {
        if (_categoryDragGhost == null) return;
        var w = _categoryDragGhost.ActualWidth > 0 ? _categoryDragGhost.ActualWidth : _categoryDragGhost.Width;
        var h = _categoryDragGhost.ActualHeight > 0 ? _categoryDragGhost.ActualHeight : _categoryDragGhost.Height;
        var left = rootX - _categoryDragGrabDX;
        var top = rootY - _categoryDragGrabDY;
        // 放大 1.045 是绕中心外扩的，占位对齐按中心补偿，视觉上不漂。
        Canvas.SetLeft(_categoryDragGhost, left - (w * 0.045) / 2);
        Canvas.SetTop(_categoryDragGhost, top - (h * 0.045) / 2);
    }

    /// <summary>光标所在槽位（可见行的平铺索引）。</summary>
    private int CategoryDropIndex(double rootY)
    {
        var top = CategoryListHost.TransformToVisual(RootGrid).TransformPoint(new Point()).Y;
        var index = (int)Math.Floor((rootY - top) / CategoryRowStride);
        return Math.Clamp(index, 0, Math.Max(0, _categoryRows.Count - 1));
    }

    /// <summary>拖拽中被隐藏的行块：分类行就是自己；集合行 = 头 + 连续子行。</summary>
    private List<CategoryRowRec> BlockOf(CategoryRowRec rec)
    {
        var block = new List<CategoryRowRec> { rec };
        if (!rec.IsFolder || rec.Folder == null) return block;
        var folderId = rec.Folder.Id;
        var index = _categoryRows.IndexOf(rec);
        if (index < 0) return block;
        for (var i = index + 1;
             i < _categoryRows.Count && !_categoryRows[i].IsFolder && _categoryRows[i].FolderId == folderId;
             i++)
            block.Add(_categoryRows[i]);
        return block;
    }

    private void MoveCategoryRowWithFlip(int target)
    {
        var rec = _categoryDragRec!;
        if (target == _categoryRows.IndexOf(rec)) return;
        ReorderRowsWithFlip(() =>
        {
            _categoryRows.Remove(rec);
            _categoryRows.Insert(Math.Clamp(target, 0, _categoryRows.Count), rec);
        }, rec);
        ApplyMembershipByPredecessor(rec);
    }

    private void MoveFolderBlockWithFlip(int target)
    {
        var rec = _categoryDragRec!;
        var block = BlockOf(rec);
        var reduced = _categoryRows.Except(block).ToList();
        var reducedTarget = Math.Clamp(target, 0, reduced.Count);
        // 集合与集合平级：目标落在别的集合头/子行区间内时顺延到该区间之后。
        while (reducedTarget < reduced.Count)
        {
            var prev = reducedTarget > 0 ? reduced[reducedTarget - 1] : null;
            if (prev == null) break;
            if (prev.IsFolder || prev.FolderId != null) { reducedTarget++; continue; }
            break;
        }
        var headerBefore = _categoryRows.TakeWhile(r => !block.Contains(r)).Count();
        if (reducedTarget == headerBefore) return;
        ReorderRowsWithFlip(() =>
        {
            foreach (var row in block) _categoryRows.Remove(row);
            for (var i = 0; i < block.Count; i++)
                _categoryRows.Insert(Math.Clamp(reducedTarget + i, 0, _categoryRows.Count), block[i]);
        }, block.ToArray());
    }

    /// <summary>
    /// 通用重排 + FLIP：mutate 改 _categoryRows，随后按 (旧索引 − 新索引)×行距
    /// 给所有位移行做滑移动画；skip 的行（被拖块）不做变换，保持布局坐标纯净。
    /// </summary>
    private void ReorderRowsWithFlip(Action mutate, params CategoryRowRec[] skip)
    {
        var oldIndex = new Dictionary<CategoryRowRec, int>();
        for (var i = 0; i < _categoryRows.Count; i++) oldIndex[_categoryRows[i]] = i;
        mutate();
        SyncHostOrder();
        for (var i = 0; i < _categoryRows.Count; i++)
        {
            var row = _categoryRows[i];
            if (skip.Contains(row)) continue;
            var shift = (oldIndex[row] - i) * CategoryRowStride;
            if (Math.Abs(shift) >= 0.5) FlipCategoryRow(row.Root, shift);
        }
    }

    /// <summary>
    /// 宿主子元素与 _categoryRows 保持同序。自愈式：缺失的元素按安全索引补插、
    /// 多出的孤儿元素清除——任何历史不同步（异常中断等）都会被修正，不会越界。
    /// </summary>
    private void SyncHostOrder()
    {
        for (var i = 0; i < _categoryRows.Count; i++)
        {
            var element = _categoryRows[i].Root;
            var current = CategoryListHost.Children.IndexOf(element);
            if (current == i) continue;
            if (current >= 0) CategoryListHost.Children.Move((uint)current, (uint)Math.Min(i, CategoryListHost.Children.Count - 1));
            else CategoryListHost.Children.Insert(Math.Min(i, CategoryListHost.Children.Count), element);
        }
        for (var i = CategoryListHost.Children.Count - 1; i >= 0 && CategoryListHost.Children.Count > _categoryRows.Count; i--)
        {
            var element = CategoryListHost.Children[i];
            // 布局预设行不属于分类行体系（在 BuildPresetRows 自管），不作孤儿清除。
            if (_presetRows.Values.Any(r => ReferenceEquals(r, element))) continue;
            if (_categoryRows.All(r => !ReferenceEquals(r.Root, element)))
                CategoryListHost.Children.RemoveAt(i);
        }
    }

    private void FlipCategoryRow(FrameworkElement container, double shift)
    {
        var residual = container.RenderTransform is TranslateTransform translate ? translate.Y : 0; // 残余动画值
        if (_categoryRowFlips.TryGetValue(container, out var running)) running.Stop();
        var t = new TranslateTransform { Y = residual + shift };
        container.RenderTransform = t;
        var story = new Storyboard();
        story.Children.Add(DoubleAnimOn(t, "Y", residual + shift, 0, 150));
        _categoryRowFlips[container] = story;
        story.Begin();
    }

    /// <summary>下方行整体滑移（折叠/展开用）：shift = 旧视觉位置 − 新布局位置。</summary>
    private void FlipRowsFrom(int startIndex, double shift)
    {
        if (Math.Abs(shift) < 0.5) return;
        for (var i = Math.Max(0, startIndex); i < _categoryRows.Count; i++)
            FlipCategoryRow(_categoryRows[i].Root, shift);
    }

    private void UpdateCategoryDragHiddenRows()
    {
        var block = BlockOf(_categoryDragRec!);
        foreach (var row in _categoryRows)
        {
            if (block.Contains(row)) row.Root.Opacity = 0;
            else if (row.Root.Opacity == 0) row.Root.Opacity = 1;
        }
    }

    private void StopCategoryDragTimer()
    {
        _categoryDragTimer?.Stop();
    }

    /// <summary>松手：行回显，幽灵飞回最终槽位淡出。</summary>
    private void SettleCategoryDrag()
    {
        var ghost = _categoryDragGhost;
        var scale = _categoryDragGhostScale;
        var rec = _categoryDragRec;
        foreach (var row in _categoryRows) row.Root.Opacity = 1; // 立刻回显，飞行淡出中自然盖合
        _categoryDragGhost = null;
        _categoryDragGhostScale = null;
        if (ghost == null) return;

        var story = new Storyboard();
        var toLeft = Canvas.GetLeft(ghost);
        var toTop = Canvas.GetTop(ghost);
        if (rec != null)
        {
            var point = rec.Root.TransformToVisual(RootGrid).TransformPoint(new Point());
            toLeft = point.X;
            toTop = point.Y;
        }
        story.Children.Add(DoubleAnimOn(ghost, "(Canvas.Left)", Canvas.GetLeft(ghost), toLeft, 200));
        story.Children.Add(DoubleAnimOn(ghost, "(Canvas.Top)", Canvas.GetTop(ghost), toTop, 200));
        story.Children.Add(DoubleAnimOn(ghost, "Opacity", ghost.Opacity, 0, 200));
        if (scale != null) // 放大回落，落进槽位时刚好与原行等大
            foreach (var path in new[] { "ScaleX", "ScaleY" })
                story.Children.Add(DoubleAnimOn(scale, path, 1.045, 1.0, 200));
        story.Completed += (_, _) => DragLayer.Children.Remove(ghost);
        story.Begin();
    }

    private void EndCategoryDrag()
    {
        StopCategoryDragTimer();
        SetDragJoinHighlight(null);
        SettleCategoryDrag();
        _categoryDragRec = null;
        _categoryDragMoving = false;
        _categoryDragActive = false;
    }
}
