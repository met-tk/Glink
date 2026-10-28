using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Glink.Core;
using Windows.Foundation;

namespace Glink;

// ====================================================================
//  画布布局预设：
//  - 保存：右下角工具条"保存布局"→ 命名 → 快照当前画布（条目集合 +
//    每张卡的坐标 / 层级 / 宽度），持久化到 layout-presets.json。
//  - 呈现：侧栏情绪意向列表末尾的特殊条目（强调色图标盒，区别于
//    普通分类的彩色圆点；不可拖拽、不可改色）。
//  - 加载：点击预设 → 画布按保存的坐标原位摆放（跳过自动紧凑平铺）；
//    普通分类不携带布局信息，行为完全不变。
//  - 管理：右键预设 → 更新为当前画布 / 重命名 / 删除。
// ====================================================================

public sealed partial class MainWindow
{
    private List<LayoutPreset> _layoutPresets = new();
    private Guid? _selectedPresetId;
    // 加载分支的待应用预设（LoadCanvasAsync 建卡时按其坐标摆放）。
    private LayoutPreset? _applyingPreset;
    private readonly Dictionary<Guid, Grid> _presetRows = new();

    private void LoadLayoutPresets() => _layoutPresets = LayoutPresetStore.Load();

    private void PersistLayoutPresets()
    {
        LayoutPresetStore.Save(_layoutPresets);
        BuildPresetRows();
    }

    // ------------------------------------------------------------------
    //  保存
    // ------------------------------------------------------------------

    private async void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EndInlineEdit(save: true);
            // 布局快照属于画布模式；列表模式下先切回平铺。
            if (_isListMode || _isWordCloudMode) SetViewMode("tile");
            var cards = CardCanvas.Children.OfType<Border>().Where(b => b.DataContext is ItemVM).ToList();
            if (cards.Count == 0) return;

            var input = new TextBox
            {
                PlaceholderText = "给这份画布布局起个名字…",
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                Background = ResourceBrush("SidebarBrush"),
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                Padding = new Thickness(10, 8, 10, 8)
            };
            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "保存画布布局",
                Content = input,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Background = (Brush)Application.Current.Resources["CardBrush"],
                Foreground = (Brush)Application.Current.Resources["TextBrush"]
            };
            if (await ThemeService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            var name = input.Text.Trim();
            if (name.Length == 0) name = $"布局 {_layoutPresets.Count + 1}";

            // 已在查看某预设时保存 = 就地更新该预设的快照。
            var preset = _selectedPresetId is Guid pid ? _layoutPresets.FirstOrDefault(p => p.Id == pid) : null;
            preset ??= new LayoutPreset { Name = name };
            preset.Name = name;
            preset.Items = CaptureCanvasLayout(cards);
            if (_layoutPresets.All(p => p.Id != preset.Id)) _layoutPresets.Add(preset);
            PersistLayoutPresets();
            UpdateContentTitle();
        }
        catch (Exception error)
        {
            ShowImportError("未能保存画布布局", error);
        }
    }

    /// <summary>快照当前画布：条目 Id + 坐标 / 层级 / 宽度（含用户手动调整的宽度）。</summary>
    private static List<LayoutPresetItem> CaptureCanvasLayout(List<Border> cards) => cards
        .Where(c => c.DataContext is ItemVM vm && vm.Id != Guid.Empty)
        .Select(c =>
        {
            var vm = (ItemVM)c.DataContext;
            return new LayoutPresetItem
            {
                Id = vm.Id,
                X = Canvas.GetLeft(c),
                Y = Canvas.GetTop(c),
                Z = Canvas.GetZIndex(c),
                W = c.ActualWidth > 0 ? c.ActualWidth : c.Width
            };
        })
        .ToList();

    // ------------------------------------------------------------------
    //  加载
    // ------------------------------------------------------------------

    private void LoadLayoutPreset(LayoutPreset preset)
    {
        _favoritesOnly = false;
        _selectedCategoryId = null;
        _selectedPresetId = preset.Id;
        AllHighlight.Visibility = Visibility.Collapsed;
        FavoritesHighlight.Visibility = Visibility.Collapsed;
        _ = RefreshItemsAsync();
    }

    /// <summary>当前是否处于布局预设视图（加载分支按保存坐标摆放，跳过自动平铺）。</summary>
    private LayoutPreset? ActiveLayoutPreset =>
        _selectedPresetId is Guid id ? _layoutPresets.FirstOrDefault(p => p.Id == id) : null;

    // ------------------------------------------------------------------
    //  侧栏特殊行（区别于普通分类：强调色图标盒、SemiBold 名称、无拖拽）
    // ------------------------------------------------------------------

    private void BuildPresetRows()
    {
        foreach (var old in _presetRows.Values.ToList())
            CategoryListHost.Children.Remove(old);
        _presetRows.Clear();
        foreach (var preset in _layoutPresets)
        {
            var captured = preset;
            var row = new Grid
            {
                Height = 44,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                // 未选中也要显式透明背景：null 背景的区域不参与 hit-test，点击会穿透。
                Background = ResourceBrush("TransparentBrush"),
                Tag = captured
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(5),
                Background = ResourceBrush("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = "\uE8A5", FontSize = 10,
                    Foreground = ResourceBrush("OnAccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            var name = new TextBlock
            {
                Text = preset.Name, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResourceBrush("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var count = new TextBlock
            {
                Text = preset.Items.Count.ToString(), FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                Foreground = ResourceBrush("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(icon);
            row.Children.Add(name);
            Grid.SetColumn(name, 1);
            row.Children.Add(count);
            Grid.SetColumn(count, 2);

            row.Tapped += (_, _) => LoadLayoutPreset(captured);
            row.RightTapped += (_, args) => ShowPresetMenu(captured, row, args);
            ToolTipService.SetToolTip(row, $"布局预设：{preset.Name}（{preset.Items.Count} 个对象）");
            AutomationProperties.SetName(row, $"布局预设 {preset.Name}");

            CategoryListHost.Children.Add(row);
            _presetRows[captured.Id] = row;
        }
        UpdatePresetRowHighlights();
    }

    private void UpdatePresetRowHighlights()
    {
        foreach (var (id, row) in _presetRows)
            row.Background = id == _selectedPresetId ? ResourceBrush("SelectionBrush") : ResourceBrush("TransparentBrush");
    }

    private void ShowPresetMenu(LayoutPreset preset, FrameworkElement target, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            return item;
        }

        var viewingThis = _selectedPresetId == preset.Id;
        var update = Item("更新为当前画布", () =>
        {
            var cards = CardCanvas.Children.OfType<Border>().Where(b => b.DataContext is ItemVM).ToList();
            if (cards.Count == 0 || !viewingThis) return;
            preset.Items = CaptureCanvasLayout(cards);
            PersistLayoutPresets();
            UpdateContentTitle();
        });
        update.IsEnabled = viewingThis;
        menu.Items.Add(update);
        menu.Items.Add(Item("重命名", () =>
            ShowCategoryEditFlyout(target, "重命名布局", "保存", preset.Name, null,
                (name, _) =>
                {
                    preset.Name = name;
                    PersistLayoutPresets();
                    UpdateContentTitle();
                    return Task.CompletedTask;
                })));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("删除布局", () =>
        {
            _layoutPresets.RemoveAll(p => p.Id == preset.Id);
            if (_selectedPresetId == preset.Id)
            {
                _selectedPresetId = null;
                _ = RefreshItemsAsync();
            }
            else PersistLayoutPresets();
        }));
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
    }
}
