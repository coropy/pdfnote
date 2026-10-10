using System;
using PdfNote.Ink;

namespace PdfNote.Core;

/// <summary>
/// オブジェクト操作の Undo/Redo。既存の <see cref="IInkOperation"/> / InkUndoStack (256 上限) を再利用し、
/// ストローク操作と同一の履歴に混在させる。
/// 各操作は生成時にキャプチャした Notebook インスタンスのみを対象とするため、
/// ノート切替 (履歴クリア済み) 後に別ノートへ作用することはない。
/// 対象オブジェクトが既に削除されている場合 (履歴の不整合)、変形 / Z 操作は無視する (安全側)。
/// </summary>
public sealed class AddPdfObjectOperation : IInkOperation
{
    public AddPdfObjectOperation(Notebook notebook, int pageIndex, PdfObject pdfObject)
    {
        Notebook = notebook;
        PageIndex = pageIndex;
        PdfObject = pdfObject;
    }

    public Notebook Notebook { get; }

    public int PageIndex { get; }

    public PdfObject PdfObject { get; }

    public void Undo(InkDocument document) => RemoveByIdentity();

    public void Redo(InkDocument document) => RestoreAtEnd();

    private void RemoveByIdentity()
    {
        if (FindPage() is { } page)
        {
            page.Objects.RemoveAll(o => o.Id == PdfObject.Id);
        }
    }

    private void RestoreAtEnd()
    {
        // 再追加はリスト末尾 (描画は Z 順 → リスト順のため表示結果は不変)。
        FindPage()?.Objects.Add(PdfObject);
    }

    private NotebookPage? FindPage() =>
        (uint)PageIndex < (uint)Notebook.Pages.Count ? Notebook.Pages[PageIndex] : null;
}

public sealed class RemovePdfObjectOperation : IInkOperation
{
    public RemovePdfObjectOperation(Notebook notebook, int pageIndex, PdfObject pdfObject, int index)
    {
        Notebook = notebook;
        PageIndex = pageIndex;
        PdfObject = pdfObject;
        Index = index;
    }

    public Notebook Notebook { get; }

    public int PageIndex { get; }

    public PdfObject PdfObject { get; }

    /// <summary>削除前のリスト内位置 (Undo で復元する)。</summary>
    public int Index { get; }

    public void Undo(InkDocument document)
    {
        var page = (uint)PageIndex < (uint)Notebook.Pages.Count ? Notebook.Pages[PageIndex] : null;
        if (page is null)
        {
            return;
        }

        var index = Math.Clamp(Index, 0, page.Objects.Count);
        page.Objects.Insert(index, PdfObject);
    }

    public void Redo(InkDocument document)
    {
        var page = (uint)PageIndex < (uint)Notebook.Pages.Count ? Notebook.Pages[PageIndex] : null;
        page?.Objects.RemoveAll(o => o.Id == PdfObject.Id);
    }
}

public sealed class TransformPdfObjectOperation : IInkOperation
{
    public TransformPdfObjectOperation(PageObject pageObject, ObjectTransform before, ObjectTransform after)
    {
        PageObject = pageObject;
        Before = before;
        After = after;
    }

    public PageObject PageObject { get; }

    public ObjectTransform Before { get; }

    public ObjectTransform After { get; }

    public void Undo(InkDocument document) => Apply(Before);

    public void Redo(InkDocument document) => Apply(After);

    private void Apply(ObjectTransform transform)
    {
        // 対象が既に削除されている (リストから外れた) 場合でも、
        // 切り離されたインスタンスへの適用は他のデータに影響しない。
        // ApplyTo は検証済みの値のみを受け付けるため部分適用は発生しない。
        transform.ApplyTo(PageObject);
    }
}

public sealed class SetObjectZOperation : IInkOperation
{
    public SetObjectZOperation(PageObject pageObject, int before, int after)
    {
        PageObject = pageObject;
        Before = before;
        After = after;
    }

    public PageObject PageObject { get; }

    public int Before { get; }

    public int After { get; }

    public void Undo(InkDocument document) => PageObject.Z = Before;

    public void Redo(InkDocument document) => PageObject.Z = After;
}
