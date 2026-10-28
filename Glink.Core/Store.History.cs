using Microsoft.Data.Sqlite;

namespace Glink.Core;

public static partial class Store
{
    public static async Task SetCategoryColorAsync(Guid id, string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
            throw new ArgumentException("请输入 #RRGGBB 格式的颜色。", nameof(hex));
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE categories SET AccentHex = @hex WHERE Id = @id";
        command.Parameters.AddWithValue("@hex", hex.ToUpperInvariant());
        command.Parameters.AddWithValue("@id", id.ToString());
        if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("分类已不存在。");
    }

    public static async Task RestoreCategoryAsync(MoodCategory category, IReadOnlyList<Guid> itemIds)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO categories (Id, Name, AccentHex, SortOrder) VALUES (@id, @name, @hex, @sort)";
        command.Parameters.AddWithValue("@id", category.Id.ToString());
        command.Parameters.AddWithValue("@name", category.Name);
        command.Parameters.AddWithValue("@hex", category.AccentHex);
        command.Parameters.AddWithValue("@sort", category.SortOrder);
        await command.ExecuteNonQueryAsync();
        foreach (var id in itemIds)
        {
            command.CommandText = "UPDATE items SET CategoryId = @id WHERE Id = @item";
            command.Parameters.AddWithValue("@item", id.ToString());
            await command.ExecuteNonQueryAsync();
            command.Parameters.RemoveAt("@item");
        }
        transaction.Commit();
    }

    private static async Task RestoreItemAsync(Item item)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items (Id,Type,Content,FilePath,FileName,SizeBytes,CategoryId,CreatedAt,Note,IsFavorite,PosX,PosY,ZIndex)
            VALUES (@id,@type,@content,@path,@name,@size,@category,@created,@note,@favorite,@x,@y,@z)
            """;
        command.Parameters.AddWithValue("@id", item.Id.ToString());
        command.Parameters.AddWithValue("@type", (int)item.Type);
        command.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("@path", (object?)item.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@name", (object?)item.FileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@size", item.SizeBytes);
        command.Parameters.AddWithValue("@category", (object?)item.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@note", (object?)item.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("@favorite", item.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@x", (object?)item.PosX ?? DBNull.Value);
        command.Parameters.AddWithValue("@y", (object?)item.PosY ?? DBNull.Value);
        command.Parameters.AddWithValue("@z", item.ZIndex);
        await command.ExecuteNonQueryAsync();
    }

    // Media is moved on the same volume, never read into memory or copied in full.
    // Only archived copies owned by this history entry are removed when it expires.
    public sealed class ItemArchive : IDisposable
    {
        private readonly Item _item;
        private readonly string _retainedPath = Path.Combine(DataDirectory, ".undo", Guid.NewGuid().ToString("N"));
        private bool _removed;
        private bool _retainedMedia;
        public ItemArchive(Item item) => _item = item;

        public async Task RemoveAsync()
        {
            if (_removed) return;
            var path = _item.FilePath == null ? null : AbsolutePath(_item.FilePath);
            var moved = false;
            try
            {
                if (path != null && File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_retainedPath)!);
                    File.Move(path, _retainedPath); moved = true;
                }
                await DeleteAsync(_item.Id);
                _retainedMedia = moved;
                _removed = true;
            }
            catch
            {
                if (moved) File.Move(_retainedPath, path!);
                throw;
            }
        }

        public async Task RestoreAsync()
        {
            if (!_removed) return;
            var path = _item.FilePath == null ? null : AbsolutePath(_item.FilePath);
            var moved = false;
            try
            {
                if (_retainedMedia && !File.Exists(_retainedPath))
                    throw new FileNotFoundException("撤回所需的媒体文件已被移动，请恢复文件后重试。", _retainedPath);
                if (path != null && File.Exists(_retainedPath)) { File.Move(_retainedPath, path); moved = true; }
                await RestoreItemAsync(_item);
                _removed = false;
            }
            catch
            {
                if (moved) File.Move(path!, _retainedPath);
                throw;
            }
        }

        public void Dispose()
        {
            try { if (_removed) File.Delete(_retainedPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
