using Glink.Core;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glink;

/// <summary>Reads one representation of a drop and isolates failures per file.</summary>
internal sealed class ExternalDropImporter
{
    private readonly Func<StorageFile, Guid?, Task<Item>> _addFile;
    private readonly Func<string, Guid?, Task<Item>> _addText;

    public ExternalDropImporter(Func<StorageFile, Guid?, Task<Item>>? addFile = null,
        Func<string, Guid?, Task<Item>>? addText = null)
    {
        _addFile = addFile ?? Store.AddFileAsync;
        _addText = addText ?? Store.AddTextAsync;
    }

    public static bool CanRead(DataPackageView data) =>
        data.Contains(StandardDataFormats.StorageItems) ||
        data.Contains(StandardDataFormats.Bitmap) ||
        data.Contains(StandardDataFormats.Text) ||
        data.Contains(StandardDataFormats.WebLink) ||
        data.Contains(StandardDataFormats.ApplicationLink) ||
        data.Contains(StandardDataFormats.Uri);

    public async Task<ImportResult> ImportAsync(DataPackageView data, Guid? categoryId,
        Action<string>? progress = null)
    {
        var result = new ImportResult();
        try
        {
            // StorageItems is authoritative. Do not turn skipped/failed files into path-text cards.
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                await ImportFilesAsync(await data.GetStorageItemsAsync(), categoryId, result, progress);
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                await ImportBitmapAsync(await data.GetBitmapAsync(), categoryId, result, progress);
            }
            else if (data.Contains(StandardDataFormats.WebLink))
            {
                await ImportUriAsync(await data.GetWebLinkAsync(), categoryId, result, progress);
            }
            else if (data.Contains(StandardDataFormats.ApplicationLink))
            {
                await ImportUriAsync(await data.GetApplicationLinkAsync(), categoryId, result, progress);
            }
            else if (data.Contains(StandardDataFormats.Uri))
            {
                await ImportUriAsync(await data.GetUriAsync(), categoryId, result, progress);
            }
            else if (data.Contains(StandardDataFormats.Text))
            {
                var text = await data.GetTextAsync();
                var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
                // Treat the payload as paths only when every nonempty line is an absolute path.
                // Otherwise preserve all whitespace and line breaks as a single text object.
                if (lines.Length > 0 && lines.All(line => TryGetLocalPath(line, out _)))
                {
                    foreach (var line in lines)
                    {
                        TryGetLocalPath(line, out var path);
                        await ImportPathAsync(path, categoryId, result, progress);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Items.Add(await _addText(text, categoryId));
                }
            }
        }
        catch (Exception error)
        {
            result.Failures.Add(new ImportFailure("拖入内容", error));
        }
        return result;
    }

    public async Task<ImportResult> ImportFilesAsync(IEnumerable<IStorageItem> files, Guid? categoryId,
        Action<string>? progress = null)
    {
        var result = new ImportResult();
        await ImportFilesAsync(files, categoryId, result, progress);
        return result;
    }

    private async Task ImportFilesAsync(IEnumerable<IStorageItem> files, Guid? categoryId,
        ImportResult result, Action<string>? progress)
    {
        foreach (var item in files)
        {
            if (item is not StorageFile file)
            {
                result.Skipped.Add($"{item.Name}（文件夹）");
                continue;
            }
            await ImportFileAsync(file, categoryId, result, progress);
        }
    }

    /// <summary>
    /// 剪贴板位图：优先按字节头识别原格式（PNG/JPG/GIF/BMP/WEBP）原样保存；
    /// 识别失败时转码为 PNG，保证复制到剪贴板的截图/位图也能入库为图片对象。
    /// </summary>
    private async Task ImportBitmapAsync(IRandomAccessStreamReference bitmap, Guid? categoryId,
        ImportResult result, Action<string>? progress)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"Glink-clipboard-{Guid.NewGuid():N}");
        try
        {
            using var source = await bitmap.OpenReadAsync();
            var extension = await SniffImageExtensionAsync(source);
            temporaryPath += extension ?? ".png";
            if (extension == null)
                await TranscodeToPngAsync(source, temporaryPath);
            else
            {
                source.Seek(0);
                using var output = File.Create(temporaryPath);
                await source.AsStreamForRead().CopyToAsync(output);
            }
            progress?.Invoke("剪贴板图片");
            result.Items.Add(await _addFile(await StorageFile.GetFileFromPathAsync(temporaryPath), categoryId));
        }
        catch (Exception error)
        {
            result.Failures.Add(new ImportFailure("剪贴板图片", error));
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* best-effort */ }
        }
    }

    private static async Task<string?> SniffImageExtensionAsync(IRandomAccessStream source)
    {
        var count = (uint)Math.Min((long)8, source.Size);
        if (count == 0) return null;
        using var reader = new DataReader(source.GetInputStreamAt(0));
        await reader.LoadAsync(count);
        var header = new byte[8];
        for (var i = 0; i < reader.UnconsumedBufferLength; i++) header[i] = reader.ReadByte();
        if (header.Length >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return ".png";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ".jpg";
        if (header.Length >= 3 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
            return ".gif";
        if (header.Length >= 2 && header[0] == 0x42 && header[1] == 0x4D)
            return ".bmp";
        if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            return ".webp";
        return null;
    }

    private static async Task TranscodeToPngAsync(IRandomAccessStream source, string path)
    {
        source.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(source);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
        softwareBitmap ??= await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = File.Create(path).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();
    }

    private async Task ImportFileAsync(StorageFile file, Guid? categoryId,
        ImportResult result, Action<string>? progress)
    {
        if (!Store.TryGetFileType(file.Name, out _))
        {
            result.Skipped.Add(file.Name);
            return;
        }
        try
        {
            progress?.Invoke(file.Name);
            result.Items.Add(await _addFile(file, categoryId));
        }
        catch (Exception error)
        {
            result.Failures.Add(new ImportFailure(file.Name, error));
        }
    }

    private async Task ImportUriAsync(Uri uri, Guid? categoryId,
        ImportResult result, Action<string>? progress)
    {
        if (uri.IsFile)
            await ImportPathAsync(uri.LocalPath, categoryId, result, progress);
        else
            result.Items.Add(await _addText(uri.AbsoluteUri, categoryId));
    }

    private async Task ImportPathAsync(string path, Guid? categoryId,
        ImportResult result, Action<string>? progress)
    {
        try
        {
            path = Path.GetFullPath(path); // WinRT requires normalized Windows path separators.
            if (Directory.Exists(path))
            {
                result.Skipped.Add($"{Path.GetFileName(path)}（文件夹）");
                return;
            }
            await ImportFileAsync(await StorageFile.GetFileFromPathAsync(path), categoryId, result, progress);
        }
        catch (Exception error)
        {
            result.Failures.Add(new ImportFailure(path, error));
        }
    }

    internal static bool TryGetLocalPath(string value, out string path)
    {
        path = value.Trim().Trim('"');
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return true;
        }
        return Path.IsPathFullyQualified(path);
    }
}

internal sealed record ImportFailure(string Name, Exception Error);

internal sealed class ImportResult
{
    public List<Item> Items { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<ImportFailure> Failures { get; } = new();
}
