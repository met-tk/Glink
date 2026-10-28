using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Glink.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace Glink;

public sealed partial class MainWindow
{
    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];

    private void InitializeAppearance()
    {
        LoadTextPrefs();
        LoadLayoutPresets();
        ThemeService.Load();
        ApplyWindowTheme();
        BuildThemeChips();
        BuildCanvasOptions();
        VersionText.Text = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        // 代码/样式赋过共享刷的文字不随刷子原地改色联动，切换主题后整体重赋一遍
        //（见 ThemeService.ReassertThemedBrushes），否则浅色主题下会残留上一主题的浅色文字。
        ThemeService.Changed += ReassertMainWindowTheme;
        Closed += (_, _) => ThemeService.Changed -= ReassertMainWindowTheme;
        RootGrid.Loaded += (_, _) =>
        {
            UpdateWindowLayout();
            RootGrid.XamlRoot.Changed += (_, _) => UpdateWindowLayout();
        };
        RootGrid.SizeChanged += (_, _) => UpdateWindowLayout();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
    }

    private void ReassertMainWindowTheme()
    {
        ThemeService.ReassertThemedBrushes(RootGrid);
        RebuildCanvasOptions(); // 药丸开关持的是构建时的刷子引用，换主题整体重建
        // 分类选中色调派生自旧主题的卡色，逐行重取一次（缓存键已随主题失效）。
        foreach (var rec in _categoryRows)
            if (rec.Category is { IsSelected: true })
                rec.Content.Background = rec.Category.Background;
    }

    // ------------------------------------------------------------------
    //  画布偏好：紧凑平铺锚点（左上角 / 画布中心）
    // ------------------------------------------------------------------

    private void BuildCanvasOptions()
    {
        if (CanvasOptions == null) return;
        CanvasOptions.Children.Clear();
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = "紧凑平铺围绕画布中心", FontSize = 14,
            Foreground = ResourceBrush("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var pill = BuildPillSwitch("紧凑平铺围绕画布中心", _centeredPacking, on =>
        {
            _centeredPacking = on;
            SaveCenteredPref();
            // 立即按新锚点重排当前视图，所见即所得。
            if (!_isListMode && CardCanvas.Children.Count > 0)
            {
                ApplyCompactLayout();
                FocusCanvasContent();
            }
        });
        row.Children.Add(label);
        row.Children.Add(pill);
        Grid.SetColumn(pill, 1);
        CanvasOptions.Children.Add(row);

        // 文本卡水平居中（默认开）：垂直居中为便签结构性设计（恒开），
        // 此开关只控制单行文本与日期的水平对齐。
        var textRow = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        textRow.ColumnDefinitions.Add(new ColumnDefinition());
        textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textLabel = new TextBlock
        {
            Text = "文本在卡片中水平居中", FontSize = 14,
            Foreground = ResourceBrush("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var textPill = BuildPillSwitch("文本在卡片中水平居中", _textCentered, on =>
        {
            _textCentered = on;
            SaveTextPrefs();
            _ = RefreshItemsAsync(); // 影响单行文本的水平对齐，整体重建所见即所得
        });
        textRow.Children.Add(textLabel);
        textRow.Children.Add(textPill);
        Grid.SetColumn(textPill, 1);
        CanvasOptions.Children.Add(textRow);

        // 文本卡片字体：跨主题菜单选择，菜单项用各自字体预览；切换后重建生效。
        var fontRow = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        fontRow.ColumnDefinitions.Add(new ColumnDefinition());
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fontLabel = new TextBlock
        {
            Text = "文本卡片字体", FontSize = 14,
            Foreground = ResourceBrush("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var fontButton = new Button { Style = (Style)Application.Current.Resources["SidebarTextStyle"], Padding = new Thickness(12, 6, 12, 6) };
        var currentFont = TextFontChoices.FirstOrDefault(c => c.Family == _textFontFamily);
        var fontName = new TextBlock { Text = currentFont.Label, FontSize = 13, Foreground = ResourceBrush("TextBrush") };
        fontButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { fontName, new FontIcon { Glyph = "\uE70D", FontSize = 10, Foreground = ResourceBrush("SubTextBrush") } }
        };
        var fontMenu = new MenuFlyout();
        foreach (var choice in TextFontChoices)
        {
            var captured = choice;
            var item = new MenuFlyoutItem
            {
                Text = choice.Label,
                FontFamily = new FontFamily(choice.Family),
                Icon = new FontIcon { Glyph = choice.Family == _textFontFamily ? "\uE73E" : "\uE739", FontSize = 12 }
            };
            item.Click += (_, _) =>
            {
                if (_textFontFamily == captured.Family) return;
                _textFontFamily = captured.Family;
                SaveTextPrefs();
                _ = RefreshItemsAsync(); // 重建卡片应用新字体（基线随之重测）
            };
            fontMenu.Items.Add(item);
        }
        fontButton.Flyout = fontMenu;
        AutomationProperties.SetName(fontButton, "文本卡片字体");
        fontRow.Children.Add(fontLabel);
        fontRow.Children.Add(fontButton);
        Grid.SetColumn(fontButton, 1);
        CanvasOptions.Children.Add(fontRow);
    }

    private void RebuildCanvasOptions() => BuildCanvasOptions();

    /// <summary>主题化药丸开关：轨道点亮用 AccentBrush，圆钮滑动 140ms。</summary>
    private FrameworkElement BuildPillSwitch(string name, bool isOn, Action<bool> onToggle)
    {
        var knob = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 3, 0)
        };
        var track = new Border
        {
            Width = 38, Height = 20, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = knob
        };
        var on = isOn;
        Render();
        void Render()
        {
            track.Background = on ? ResourceBrush("AccentBrush") : ResourceBrush("SidebarBrush");
            track.BorderBrush = ResourceBrush("BorderBrush");
            knob.Background = on ? ResourceBrush("OnAccentBrush") : ResourceBrush("TextBrush");
            var to = on ? 18 : 0; // 轨道 38 − 钮 14 − 边距 6 = 18
            var slide = new TranslateTransform { X = (knob.RenderTransform as TranslateTransform)?.X ?? 0 };
            knob.RenderTransform = slide;
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            story.Children.Add(DoubleAnimOn(slide, "X", slide.X, to, 140));
            story.Begin();
        }
        track.Tapped += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            Render();
            onToggle(on);
        };
        ToolTipService.SetToolTip(track, name);
        AutomationProperties.SetName(track, name);
        return track;
    }

    private readonly List<(MoodTheme Theme, Button Button, Border Ring, TextBlock Name)> _themeChips = new();
    private readonly TextBlock _themeDescription = new()
    {
        FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = ResourceBrush("SubTextBrush"), Margin = new Thickness(2, 4, 2, 0)
    };

    private void BuildThemeChips()
    {
        // 面板内容宽约 363px：一行四枚每枚约 85px，再多就换行而不是压缩。
        var rowHost = new StackPanel { Spacing = 8 };
        Grid? chips = null;
        var column = 0;
        foreach (var theme in ThemeService.Themes)
        {
            if (column % 4 == 0)
            {
                chips = new Grid { ColumnSpacing = 8 };
                var remaining = ThemeService.Themes.Count - column;
                for (var i = 0; i < Math.Min(4, remaining); i++) chips.ColumnDefinitions.Add(new ColumnDefinition());
                rowHost.Children.Add(chips);
            }
            var preview = new Grid { Height = 24 };
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            preview.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6, 0, 0, 6),
                Background = new SolidColorBrush(CategoryVM.HexToColor(theme.Sidebar))
            });
            var stage = new Grid { Background = new SolidColorBrush(CategoryVM.HexToColor(theme.Background)) };
            stage.Children.Add(new Border
            {
                Background = new SolidColorBrush(CategoryVM.HexToColor(theme.Card)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 6, 4, 6)
            });
            Grid.SetColumn(stage, 1);
            preview.Children.Add(stage);
            var accentStripe = new Border
            {
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                Background = new SolidColorBrush(CategoryVM.HexToColor(theme.Accent))
            };
            Grid.SetColumn(accentStripe, 2);
            preview.Children.Add(accentStripe);

            var name = new TextBlock
            {
                Text = theme.Name, FontSize = 12, TextAlignment = TextAlignment.Center,
                Foreground = ResourceBrush("SubTextBrush"), Margin = new Thickness(0, 4, 0, 0)
            };
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(preview);
            Grid.SetRow(name, 1);
            content.Children.Add(name);

            var ring = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(5), Child = content
            };
            var chip = new Button
            {
                Content = ring, Tag = theme,
                Background = ResourceBrush("TransparentBrush"),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            AutomationProperties.SetName(chip, theme.Name + "主题");
            ToolTipService.SetToolTip(chip, theme.Description);
            chip.Click += (_, _) => SelectTheme(theme);
            Grid.SetColumn(chip, column % 4);
            chips!.Children.Add(chip);
            _themeChips.Add((theme, chip, ring, name));
            column++;
        }
        ThemeOptions.Children.Add(rowHost);
        ThemeOptions.Children.Add(_themeDescription);
        UpdateThemeChipSelection(ThemeService.Current);
    }

    private void UpdateThemeChipSelection(MoodTheme selected)
    {
        foreach (var (theme, button, ring, name) in _themeChips)
        {
            var isSelected = theme.Id == selected.Id;
            ring.BorderBrush = isSelected ? ResourceBrush("AccentBrush") : ResourceBrush("BorderBrush");
            ring.Background = isSelected ? ResourceBrush("SelectionBrush") : ResourceBrush("TransparentBrush");
            name.Foreground = isSelected ? ResourceBrush("AccentBrush") : ResourceBrush("SubTextBrush");
            name.FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            AutomationProperties.SetItemStatus(button, isSelected ? "已选择" : "未选择");
        }
        CurrentThemeName.Text = selected.Name;
        _themeDescription.Text = selected.Name + " · " + selected.Description;
    }

    private void ThemePickerToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ThemeOptions == null) return;
        var expanded = ThemePickerToggle.IsChecked == true;
        ThemeOptions.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ThemePickerChevron.Glyph = expanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(ThemePickerToggle, expanded ? "收起外观主题" : "展开外观主题");
    }

    private void SelectTheme(MoodTheme theme)
    {
        var version = ++_themeSelectionVersion;
        // Let the chip finish its visual-state transition before changing theme.
        _dispatcher.TryEnqueue(() =>
        {
            if (_isClosed || version != _themeSelectionVersion) return;
            try
            {
                ThemeService.Apply(theme);
                ApplyWindowTheme();
                UpdateThemeChipSelection(theme);
                try
                {
                    ThemeService.Save();
                    ThemeSaveStatus.Text = "";
                    ThemeSaveStatus.Visibility = Visibility.Collapsed;
                }
                catch (Exception error)
                {
                    ThemeSaveStatus.Text = "主题已生效，但未能保存。请检查本地存储空间后重试。";
                    ThemeSaveStatus.Visibility = Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine(error);
                }
            }
            catch (Exception error)
            {
                ThemeSaveStatus.Text = "未能切换主题，请重试。";
                ThemeSaveStatus.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine(error);
            }
        });
    }

    private void ApplyWindowTheme()
    {
        RootGrid.RequestedTheme = ThemeService.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        UpdateCaptionColors();
    }

    private void UpdateCaptionColors()
    {
        var theme = ThemeService.Current;
        var title = AppWindow.TitleBar;
        title.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonForegroundColor = CategoryVM.HexToColor(theme.Text);
        title.ButtonInactiveForegroundColor = CategoryVM.HexToColor(theme.Secondary);
        title.ButtonHoverBackgroundColor = CategoryVM.HexToColor(theme.Selection);
        title.ButtonHoverForegroundColor = CategoryVM.HexToColor(theme.Text);
        title.ButtonPressedBackgroundColor = CategoryVM.HexToColor(theme.Border);
        title.ButtonPressedForegroundColor = CategoryVM.HexToColor(theme.Text);
    }

    private void UpdateWindowLayout()
    {
        if (RootGrid.XamlRoot == null) return;
        // 沉浸模式下标题栏/内容头都已隐藏，非客户区命中区域不再设置。
        if (_isImmersive) return;
        var narrow = FilterRow.ActualWidth < 700;
        Grid.SetRow(TimeFilters, narrow ? 1 : 0);
        Grid.SetColumn(TimeFilters, narrow ? 0 : 1);
        SettingsPanel.Width = Math.Min(420, Math.Max(280, RootGrid.ActualWidth - 240));

        // Native non-client regions include the full top strip and unused header space.
        // Interactive controls keep their client hit-testing, including at non-100% DPI.
        var scale = RootGrid.XamlRoot.RasterizationScale;
        RectInt32 Pixels(Rect r) => new((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale),
            Math.Max(0, (int)Math.Round(r.Width * scale)), Math.Max(0, (int)Math.Round(r.Height * scale)));
        var header = ContentHeader.TransformToVisual(RootGrid).TransformPoint(new Point());
        var add = AddInspirationBtn.TransformToVisual(RootGrid).TransformPoint(new Point());
        var regions = new[]
        {
            Pixels(new Rect(0,0,Math.Max(0,RootGrid.ActualWidth - AppWindow.TitleBar.RightInset / scale),TitleBarRegion.ActualHeight)),
            Pixels(new Rect(header.X,header.Y,Math.Max(0,add.X-header.X-12),ContentHeader.ActualHeight))
        };
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Caption, regions);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        EndInlineEdit(save: true);
        CancelShortcutRecording();
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            ThemePickerToggle.IsChecked = false;
            CloseSettingsButton.Focus(FocusState.Programmatic);
        }
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void CloseSettings()
    {
        CancelShortcutRecording();
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsButton.Focus(FocusState.Programmatic);
    }

    private int _themeSelectionVersion;

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 快照录制状态：绑定成功的同一事件里 _recordingShortcutId 已被清空，
        // 根级空格分支若只看当前状态会把"把空格设为快捷键"的按键误当成播放切换。
        var wasRecording = _recordingShortcutId != null;
        // While recording a shortcut, keys are captured at window level so the
        // gesture lands no matter where keyboard focus ended up.
        if (wasRecording)
        {
            ShortcutKey_RecordingKeyDown(sender, e);
            return;
        }
        if (_history.IsBusy) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (e.Handled) return;
            if (_resize != null) { EndResizeSession(null); e.Handled = true; }
            else if (_pendingDragCard != null) { CancelCanvasDrag(); e.Handled = true; }
            else if (HasSelection) { ClearSelection(); e.Handled = true; }
            else if (SettingsPanel.Visibility == Visibility.Visible) { CloseSettings(); e.Handled = true; }
            else if (_isImmersive) { ExitImmersive(); e.Handled = true; }
            return;
        }
        // F11 全屏为固定操作（与 Esc、Ctrl+滚轮同列），独立于沉浸模式。
        if (e.Key == Windows.System.VirtualKey.F11)
        {
            if (!e.Handled) { e.Handled = true; ToggleFullScreen(); }
            return;
        }
        if (IsTypingOrDialog(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject)) return;
        var ctrl = ControlIsDown;
        var shift = ShiftIsDown;
        var alt = AltIsDown;
        if (e.Handled) return;
        foreach (var command in ShortcutService.Commands)
        {
            if (!ShortcutService.GestureOf(command.Id).Matches(e.Key, ctrl, shift, alt)) continue;
            e.Handled = true;
            await ExecuteShortcutAsync(command.Id);
            return;
        }
    }

    // 空格在预览（隧道）阶段路由：预览事件先于焦点元素，抢在已聚焦按钮/芯片的
    // 默认空格激活之前消费按键，一次空格只产生一次播放切换。
    // 之前在 KeyDown 冒泡阶段拦截，按钮已先一步记住空格并在抬起时触发 Click，
    // 造成"一次空格、两次切换"的时灵时不灵。
    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 录制快捷键时把空格留给录制链路；输入框与对话框里空格保持原生行为。
        if (_recordingShortcutId != null) return;
        if (e.Key != Windows.System.VirtualKey.Space || ControlIsDown || ShiftIsDown || AltIsDown) return;
        if (IsTypingOrDialog(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject)) return;
        e.Handled = true;
        ToggleActivePlayback();
    }

    // ====================================================================
    //  Customizable shortcuts: settings UI + gesture recording
    // ====================================================================

    private string? _recordingShortcutId;
    private KeyEventHandler? _recordingKeyHandler;

    private void InitializeShortcuts()
    {
        _recordingKeyHandler = ShortcutKey_RecordingKeyDown;
        ShortcutService.Load();
        ShortcutService.Changed += OnShortcutsChanged;
        Closed += (_, _) => ShortcutService.Changed -= OnShortcutsChanged;
        BuildShortcutSettings();
        RefreshShortcutTooltips();
    }

    private void OnShortcutsChanged() => _dispatcher.TryEnqueue(RefreshShortcutTooltips);

    private void RefreshShortcutTooltips()
    {
        UpdateHistoryButtons();
        ToolTipService.SetToolTip(FavoritesButton, $"收藏 ({ShortcutService.GestureOf("viewFavorites").Display})");
        ToolTipService.SetToolTip(PackBtn, $"紧凑平铺 ({ShortcutService.GestureOf("pack").Display})");
    }

    private void BuildShortcutSettings()
    {
        ShortcutList.Children.Clear();
        foreach (var command in ShortcutService.Commands)
        {
            var row = new Grid { MinHeight = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = command.Name, FontSize = 13,
                Foreground = ResourceBrush("TextBrush"), VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(name);
            var keyButton = new Button
            {
                Tag = command.Id,
                Content = ShortcutService.GestureOf(command.Id).Display,
                MinWidth = 148, FontSize = 12,
                Background = ResourceBrush("TransparentBrush"),
                BorderBrush = ResourceBrush("BorderBrush"),
                Foreground = ResourceBrush("SubTextBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(keyButton, "录制" + command.Name + "快捷键");
            keyButton.Click += ShortcutKeyButton_Click;
            Grid.SetColumn(keyButton, 1);
            row.Children.Add(keyButton);
            ShortcutList.Children.Add(row);
        }
    }

    private void ShortcutKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        CancelShortcutRecording();
        _recordingShortcutId = id;
        button.Content = "按下新组合键…";
        button.Foreground = ResourceBrush("AccentBrush");
        button.BorderBrush = ResourceBrush("AccentBrush");
        button.AddHandler(UIElement.KeyDownEvent, _recordingKeyHandler, true);
        button.Focus(FocusState.Programmatic);
        ShortcutStatus.Text = "正在录制「" + ShortcutService.Commands.First(c => c.Id == id).Name + "」，按 Esc 取消。";
    }

    private void CancelShortcutRecording()
    {
        if (_recordingShortcutId == null) return;
        _recordingShortcutId = null;
        foreach (var child in ShortcutList.Children.OfType<Grid>().ToArray())
            foreach (var button in child.Children.OfType<Button>().ToArray())
                if (_recordingKeyHandler != null) button.RemoveHandler(UIElement.KeyDownEvent, _recordingKeyHandler);
        // Rebuilding the list while the record button is still the source of the
        // in-flight key/click event crashes XAML; let the event routing finish first.
        _dispatcher.TryEnqueue(() => { if (_recordingShortcutId == null) BuildShortcutSettings(); });
        ShortcutStatus.Text = " ";
    }

    private void ShortcutKey_RecordingKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var id = _recordingShortcutId;
        if (id == null) return;
        var key = e.Key;
        if (key is Windows.System.VirtualKey.Escape)
        {
            CancelShortcutRecording();
            return;
        }
        if (key is Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows)
        {
            ShortcutStatus.Text = "请避免使用 Win 键组合，换个组合试试。";
            return;
        }
        if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl
            or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift
            or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu)
            return; // Modifier keys alone wait for the main key.
        if (key is Windows.System.VirtualKey.NumberKeyLock or Windows.System.VirtualKey.Scroll or Windows.System.VirtualKey.CapitalLock
            or Windows.System.VirtualKey.Insert or Windows.System.VirtualKey.Print or (Windows.System.VirtualKey)44
            or Windows.System.VirtualKey.Pause or Windows.System.VirtualKey.Sleep)
        {
            ShortcutStatus.Text = "该键不适合作为快捷键，请换一个组合。";
            return;
        }
        var gesture = new KeyGesture(key, ControlIsDown, ShiftIsDown, AltIsDown);
        var command = ShortcutService.Commands.First(c => c.Id == id);
        var current = ShortcutService.GestureOf(id);
        if (gesture.Matches(current.Key, current.Ctrl, current.Shift, current.Alt))
        {
            CancelShortcutRecording();
            ShortcutStatus.Text = "「" + command.Name + "」的快捷键未更改。";
            return;
        }
        var conflict = ShortcutService.FindConflict(gesture, id);
        if (conflict != null)
        {
            CancelShortcutRecording();
            ShortcutStatus.Text = "该组合已被「" + conflict.Name + "」占用，请换一个。";
            return;
        }
        _recordingShortcutId = null;
        ShortcutService.SetOverride(id, gesture);
        ShortcutStatus.Text = "已将「" + command.Name + "」设为 " + gesture.Display + "。";
        _dispatcher.TryEnqueue(() => { if (_recordingShortcutId == null) BuildShortcutSettings(); });
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        CancelShortcutRecording();
        ShortcutService.ResetAll();
        ShortcutStatus.Text = "已恢复全部默认快捷键。";
        _dispatcher.TryEnqueue(BuildShortcutSettings);
    }
}
