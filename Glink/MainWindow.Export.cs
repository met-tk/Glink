using Glink.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Glink;

public sealed partial class MainWindow
{
    private bool _isExporting;
    private CancellationTokenSource? _exportCancellation;
    private Task _exportCompletion = Task.CompletedTask;

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting) return;
        _isExporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            await _storeInitialization;
            EndInlineEdit(save: true);
            await _textSaveTask;
            var items = await Store.GetItemsAsync(null, null, null, false);
            var categories = await Store.GetCategoriesAsync();
            var folders = _groups.Folders.Select(f => new ExportFolder(f.Id, f.Name, ChildrenOf(f.Id))).ToList();
            var request = await ChooseExportAsync(items, categories, folders);
            if (request == null) return;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, CommitButtonText = "导出到此处" };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSingleFolderAsync();
            if (destination == null) return;
            await ExportToFolderAsync(destination.Path, items, categories, folders, request);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowImportError("导出未完成，请检查保存位置和可用空间后重试", error); }
        finally
        {
            _isExporting = false;
            ExportButton.IsEnabled = true;
        }
    }

    private async Task BatchExportSelectedAsync()
    {
        if (_isExporting) return;
        _isExporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            await BatchExportSelectedCoreAsync();
        }
        finally
        {
            _isExporting = false;
            ExportButton.IsEnabled = true;
        }
    }

    private async Task BatchExportSelectedCoreAsync()
    {
        EndInlineEdit(save: true);
        await _textSaveTask;
        var selectedIds = _selectedItemIds.ToHashSet();
        var items = await Store.GetItemsAsync(null, null, null, false);
        var selected = items.Where(item => selectedIds.Contains(item.Id)).ToList();
        if (selected.Count == 0) return;

        var categories = await Store.GetCategoriesAsync();
        var folders = _groups.Folders.Select(f => new ExportFolder(f.Id, f.Name, ChildrenOf(f.Id))).ToList();
        var request = new ExportRequest(ExportScope.All, Array.Empty<ItemType>(), Array.Empty<Guid?>(),
            Array.Empty<Guid>(), selectedIds);
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, CommitButtonText = "导出到此处" };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var destination = await picker.PickSingleFolderAsync();
        if (destination == null) return;
        await ExportToFolderAsync(destination.Path, items, categories, folders, request);
        ClearSelection();
    }

    private async Task ExportToFolderAsync(string destination, IReadOnlyList<Item> items,
        IReadOnlyList<MoodCategory> categories, IReadOnlyList<ExportFolder> folders, ExportRequest request)
    {
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        var total = ExportService.Select(items, request, folders).Count;
        var status = new TextBlock { Text = "正在准备导出…", Foreground = ResourceBrush("SubTextBrush") };
        var bar = new ProgressBar { Minimum = 0, Maximum = total,
            Foreground = ResourceBrush("AccentBrush"), Background = ResourceBrush("SelectionBrush") };
        var content = new StackPanel { Spacing = 16, MinWidth = 320 };
        content.Children.Add(status);
        content.Children.Add(bar);
        var progressDialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "正在导出灵感", Content = content, CloseButtonText = "取消" };
        var finished = false;
        progressDialog.Closing += (_, args) =>
        {
            if (finished) return;
            args.Cancel = true;
            cancellation.Cancel();
            progressDialog.CloseButtonText = "正在取消…";
            status.Text = "正在取消并清理临时文件…";
        };
        var progress = new Progress<ExportProgress>(p =>
        {
            if (finished || cancellation.IsCancellationRequested) return;
            status.Text = $"已处理 {p.Completed} / {p.Total} 条灵感";
            bar.Value = p.Completed;
        });
        var showing = ThemeService.ShowDialogAsync(progressDialog);
        var exportTask = Task.Run(() => ExportService.ExportAsync(destination, Store.DataDirectory,
            items, categories, folders, request, progress, cancellation.Token));
        _exportCompletion = exportTask;
        ExportResult result;
        try
        {
            result = await exportTask;
        }
        finally
        {
            _exportCancellation = null;
            _exportCompletion = Task.CompletedTask;
            finished = true;
            progressDialog.Hide();
            await showing;
        }
        if (_isClosed) return;
        var summary = new StackPanel { Spacing = 12 };
        var textSummary = result.TextCount > 0 ? $"{result.TextCount} 条文本已合并为一个 TXT 文件。" : "所选范围不含文本。";
        summary.Children.Add(new TextBlock { Text = textSummary + $"{result.MediaCount} 个媒体文件保持原格式。",
            TextWrapping = TextWrapping.Wrap, Foreground = ResourceBrush("TextBrush") });
        if (result.Failures.Count > 0)
            summary.Children.Add(new TextBlock { Text = $"有 {result.Failures.Count} 个文件未能导出，详情见「未导出文件.txt」。原始资料未更改。",
                TextWrapping = TextWrapping.Wrap, Foreground = ResourceBrush("SubTextBrush") });
        summary.Children.Add(new TextBlock { Text = result.Directory, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, Foreground = ResourceBrush("SubTextBrush") });
        var done = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = result.Failures.Count == 0 ? "导出完成" : "导出完成，部分文件缺失",
            Content = summary, PrimaryButtonText = "打开文件夹", CloseButtonText = "完成", DefaultButton = ContentDialogButton.Primary };
        if (await ThemeService.ShowDialogAsync(done) == ContentDialogResult.Primary)
        {
            try { await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(result.Directory)); }
            catch (Exception error) { ShowImportError("导出已完成，但无法打开文件夹", error); }
        }
    }

    private async Task<ExportRequest?> ChooseExportAsync(IReadOnlyList<Item> items,
        IReadOnlyList<MoodCategory> categories, IReadOnlyList<ExportFolder> folders)
    {
        var scope = ExportScope.All;
        var checks = new List<CheckBox>();
        var body = new StackPanel { Spacing = 16, MinWidth = 360, MaxWidth = 440 };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var options = new StackPanel { Spacing = 2 };
        var counter = new TextBlock { Foreground = ResourceBrush("AccentBrush"), TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock { Text = "文本按时间先后合并为「灵感文本.txt」，每条保留创建时间和情绪分类。图片、音频与视频按原格式逐个保存。",
            Foreground = ResourceBrush("SubTextBrush"), TextWrapping = TextWrapping.Wrap };
        var structure = new TextBlock { Foreground = ResourceBrush("SubTextBrush"), TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "导出灵感", Content = body,
            PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        ExportRequest Request() => new(scope,
            checks.Where(c => c.IsChecked == true && c.Tag is ItemType).Select(c => (ItemType)c.Tag).ToArray(),
            checks.Where(c => c.IsChecked == true).Select(c => c.Tag is Guid id ? (Guid?)id : null).ToArray(),
            checks.Where(c => c.IsChecked == true && c.Tag is Guid).Select(c => (Guid)c.Tag).ToArray());
        void UpdateCount()
        {
            var selected = ExportService.Select(items, Request(), folders);
            var texts = selected.Count(i => i.Type == ItemType.Text);
            counter.Text = $"将导出 {selected.Count} 条灵感 · 文本 {texts} · 媒体 {selected.Count - texts}";
            dialog.IsPrimaryButtonEnabled = selected.Count > 0;
        }
        void AddOption(string name, object? tag, int count)
        {
            var check = new CheckBox { Content = $"{name}（{count}）", Tag = tag, IsChecked = true, Foreground = ResourceBrush("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(check, $"{name}，{count} 条灵感");
            check.Checked += (_, _) => UpdateCount();
            check.Unchecked += (_, _) => UpdateCount();
            checks.Add(check);
            options.Children.Add(check);
        }
        void SetScope(ExportScope value)
        {
            scope = value;
            foreach (var button in modes.Children.Cast<ToggleButton>()) button.IsChecked = (ExportScope)button.Tag == scope;
            checks.Clear();
            options.Children.Clear();
            if (scope == ExportScope.Type)
                foreach (var type in Enum.GetValues<ItemType>()) AddOption(ExportService.TypeName(type), type, items.Count(i => i.Type == type));
            if (scope == ExportScope.Category)
            {
                foreach (var category in categories) AddOption(category.Name, category.Id, items.Count(i => i.CategoryId == category.Id));
                AddOption("未分类", null, items.Count(i => i.CategoryId == null));
            }
            if (scope == ExportScope.Folder)
            {
                foreach (var folder in folders) AddOption(folder.Name, folder.Id, items.Count(i => i.CategoryId is Guid id && folder.CategoryIds.Contains(id)));
                if (folders.Count == 0) options.Children.Add(new TextBlock { Text = "还没有分类文件夹。可以选择按情绪分类或全部导出。",
                    Foreground = ResourceBrush("SubTextBrush"), TextWrapping = TextWrapping.Wrap });
            }
            structure.Text = scope switch
            {
                ExportScope.All => "导出整个灵感库，不受当前页面筛选影响。",
                ExportScope.Type => "可多选类型；媒体按类型分文件夹保存。",
                ExportScope.Category => "可多选情绪分类；媒体按分类分文件夹保存。",
                _ => "可多选文件夹；包含其中的全部分类，保留文件夹与分类层级。"
            };
            UpdateCount();
        }
        foreach (var (value, label) in new[] { (ExportScope.All, "全部"), (ExportScope.Type, "按类型"), (ExportScope.Category, "按情绪"), (ExportScope.Folder, "按文件夹") })
        {
            var button = new ToggleButton { Content = label, Tag = value, Style = (Style)Application.Current.Resources["FilterChipStyle"] };
            button.Checked += (_, _) => { if (scope != value) SetScope(value); };
            button.Click += (_, _) => button.IsChecked = true;
            modes.Children.Add(button);
        }
        body.Children.Add(modes);
        body.Children.Add(structure);
        body.Children.Add(new ScrollViewer { Content = options, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(counter);
        body.Children.Add(hint);
        body.Children.Add(new TextBlock { Text = "将在所选位置创建独立导出文件夹，不覆盖已有文件。", Foreground = ResourceBrush("SubTextBrush"), TextWrapping = TextWrapping.Wrap });
        SetScope(ExportScope.All);
        return await ThemeService.ShowDialogAsync(dialog) == ContentDialogResult.Primary ? Request() : null;
    }
}
