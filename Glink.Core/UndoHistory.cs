namespace Glink.Core;

/// <summary>A bounded session history. Failed operations remain available for retry.</summary>
public sealed class UndoHistory : IDisposable
{
    private sealed record Change(string Name, Func<Task> Undo, Func<Task> Redo, IDisposable? Retained);
    private readonly List<Change> _undo = new();
    private readonly List<Change> _redo = new();
    public bool IsBusy { get; private set; }
    public bool CanUndo => !IsBusy && _undo.Count > 0;
    public bool CanRedo => !IsBusy && _redo.Count > 0;
    public string? UndoName => _undo.LastOrDefault()?.Name;
    public string? RedoName => _redo.LastOrDefault()?.Name;
    public event Action? Changed;

    public void Record(string name, Func<Task> undo, Func<Task> redo, IDisposable? retained = null)
    {
        if (IsBusy) throw new InvalidOperationException("请等待撤回操作完成。");
        foreach (var change in _redo) change.Retained?.Dispose();
        _redo.Clear();
        _undo.Add(new Change(name, undo, redo, retained));
        if (_undo.Count > 100) { _undo[0].Retained?.Dispose(); _undo.RemoveAt(0); }
        Changed?.Invoke();
    }

    public Task UndoAsync() => ApplyAsync(_undo, _redo, true);
    public Task RedoAsync() => ApplyAsync(_redo, _undo, false);

    private async Task ApplyAsync(List<Change> source, List<Change> target, bool undo)
    {
        if (IsBusy || source.Count == 0) return;
        IsBusy = true; Changed?.Invoke();
        try
        {
            var change = source[^1];
            await (undo ? change.Undo() : change.Redo());
            source.RemoveAt(source.Count - 1); target.Add(change);
        }
        finally { IsBusy = false; Changed?.Invoke(); }
    }

    public void Dispose()
    {
        foreach (var change in _undo.Concat(_redo)) change.Retained?.Dispose();
        _undo.Clear(); _redo.Clear();
    }
}
