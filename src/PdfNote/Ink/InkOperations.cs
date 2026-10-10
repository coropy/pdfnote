using System;
using System.Collections.Generic;

namespace PdfNote.Ink;

/// <summary>Undo/Redo可能な操作単位。</summary>
public interface IInkOperation
{
    void Redo(InkDocument document);
    void Undo(InkDocument document);
}

public sealed class AddStrokeOperation : IInkOperation
{
    public AddStrokeOperation(Stroke stroke) => Stroke = stroke;

    public Stroke Stroke { get; }

    public void Redo(InkDocument document)
    {
        document.GetOrCreatePage(Stroke.PageIndex).Strokes.Add(Stroke);
    }

    public void Undo(InkDocument document)
    {
        var page = document.TryGetPage(Stroke.PageIndex);
        page?.Strokes.Remove(Stroke);
    }
}

public sealed class RemoveStrokeOperation : IInkOperation
{
    public RemoveStrokeOperation(Stroke stroke, int index)
    {
        Stroke = stroke;
        Index = index;
    }

    public Stroke Stroke { get; }
    public int Index { get; }

    public void Undo(InkDocument document)
    {
        var page = document.GetOrCreatePage(Stroke.PageIndex);
        var index = Math.Clamp(Index, 0, page.Strokes.Count);
        page.Strokes.Insert(index, Stroke);
    }

    public void Redo(InkDocument document)
    {
        document.TryGetPage(Stroke.PageIndex)?.Strokes.Remove(Stroke);
    }
}

/// <summary>Undo / Redo スタック (操作数上限つき)。</summary>
public sealed class InkUndoStack
{
    private readonly int _maxOperations;
    private readonly Stack<IInkOperation> _undo = new();
    private readonly Stack<IInkOperation> _redo = new();

    public InkUndoStack(int maxOperations = 256)
    {
        _maxOperations = maxOperations;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IInkOperation operation)
    {
        _undo.Push(operation);
        if (_undo.Count > _maxOperations)
        {
            // 最古の操作を捨てる (Stackは末尾取り出しのため再構築が必要)。
            var items = _undo.ToArray();
            _undo.Clear();
            for (var i = items.Length - 1; i >= 1; i--)
            {
                _undo.Push(items[i]);
            }
        }
        _redo.Clear();
    }

    public IInkOperation? Undo(InkDocument document)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var operation = _undo.Pop();
        operation.Undo(document);
        _redo.Push(operation);
        return operation;
    }

    public IInkOperation? Redo(InkDocument document)
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var operation = _redo.Pop();
        operation.Redo(document);
        _undo.Push(operation);
        return operation;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
