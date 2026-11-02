namespace DesktopNotes.Desktop.Services;

public enum UndoActionType
{
    Reparent,
    Trash,
    Close,
    Restore,
    Other
}

public sealed record UndoEntry(
    string Description,
    UndoActionType ActionType,
    Func<CancellationToken, Task> UndoFunc,
    DateTimeOffset Timestamp);

public sealed class UndoService
{
    private static UndoService? s_instance;
    public static UndoService Instance => s_instance ??= new UndoService();

    private readonly Stack<UndoEntry> _undoStack = new();
    private const int MaxEntries = 25;

    public event EventHandler? StateChanged;

    public bool CanUndo => _undoStack.Count > 0;

    public string? LastActionDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;

    public IReadOnlyList<UndoEntry> RecentEntries => _undoStack.ToArray();

    public void Push(string description, UndoActionType actionType, Func<CancellationToken, Task> undoFunc)
    {
        _undoStack.Push(new UndoEntry(description, actionType, undoFunc, DateTimeOffset.UtcNow));
        while (_undoStack.Count > MaxEntries)
        {
            var list = _undoStack.ToList();
            list.RemoveAt(list.Count - 1);
            _undoStack.Clear();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                _undoStack.Push(list[i]);
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> UndoAsync(CancellationToken ct)
    {
        if (_undoStack.Count == 0)
        {
            return false;
        }

        var entry = _undoStack.Pop();
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await entry.UndoFunc(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Clear()
    {
        _undoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
