namespace PdfEditor.Core.Services;

public class UndoRedoService
{
    private readonly LinkedList<string> _snapshots = new();
    private LinkedListNode<string>? _current;
    private const int MaxSnapshots = 50;

    public bool CanUndo => _current?.Previous != null;
    public bool CanRedo => _current?.Next != null;

    /// <summary>
    /// Saves a snapshot of the working copy before an edit.
    /// </summary>
    public void SaveSnapshot(string workingCopyPath)
    {
        // Remove redo branch (everything after _current)
        if (_current != null)
        {
            while (_current.Next != null)
            {
                var toRemove = _current.Next;
                TryDeleteFile(toRemove.Value);
                _snapshots.Remove(toRemove);
            }
        }

        // Enforce max snapshots by removing oldest
        while (_snapshots.Count >= MaxSnapshots)
        {
            var oldest = _snapshots.First!;
            TryDeleteFile(oldest.Value);
            _snapshots.RemoveFirst();
        }

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"undo_{Guid.NewGuid():N}.pdf");
        File.Copy(workingCopyPath, snapshotPath, overwrite: true);

        _snapshots.AddLast(snapshotPath);
        _current = _snapshots.Last;
    }

    /// <summary>
    /// Restores the previous snapshot (undo).
    /// </summary>
    public void Undo(string workingCopyPath)
    {
        if (!CanUndo) return;

        _current = _current!.Previous;
        File.Copy(_current!.Value, workingCopyPath, overwrite: true);
    }

    /// <summary>
    /// Restores the next snapshot (redo).
    /// </summary>
    public void Redo(string workingCopyPath)
    {
        if (!CanRedo) return;

        _current = _current!.Next;
        File.Copy(_current!.Value, workingCopyPath, overwrite: true);
    }

    /// <summary>
    /// Clears all snapshots and deletes temp files.
    /// </summary>
    public void Clear()
    {
        foreach (var path in _snapshots)
            TryDeleteFile(path);

        _snapshots.Clear();
        _current = null;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
