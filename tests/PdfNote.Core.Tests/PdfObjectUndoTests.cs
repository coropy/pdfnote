using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ2: オブジェクト操作の Undo/Redo と混在履歴の検証。</summary>
internal static class PdfObjectUndoTests
{
    internal static (NotebookSession Session, Notebook Notebook, InkUndoStack Stack, InkDocument Document)
        CreateWithStrokes()
    {
        var (session, notebook, stack) = PdfObjectOperationTests.Create(pageCount: 1);
        var document = new InkDocument();
        document.AttachPage(0, notebook.Pages[0].Ink);
        return (session, notebook, stack, document);
    }

    internal static Stroke AddStroke(Notebook notebook, InkDocument document, float x, float y)
    {
        var stroke = new Stroke(0, InkTool.Pen, 2f, 0, 0, 0, 255, 1);
        stroke.Points.Add(new InkPoint(new Vector2(x, y), 0.5f));
        document.GetOrCreatePage(0).Strokes.Add(stroke);
        return stroke;
    }

    internal static void UndoRedoAddRemoveTransformZ(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectUndo_AddRemoveTransformZ");
        var (session, _, stack, document) = CreateWithStrokes();
        var resourceId = session.Notebook.Resources[0].Id;

        // 追加 → Undo → Redo。
        var added = session.AddPdfObject(resourceId, 0, new ObjectTransform(1f, 2f, 100f, 50f, 0));
        var obj = added.Object!;
        check(stack.Undo(document) is AddPdfObjectOperation &&
              session.Notebook.Pages[0].Objects.Count == 0, "追加の Undo で削除");
        stack.Redo(document);
        check(session.Notebook.Pages[0].Objects.Count == 1 &&
              ReferenceEquals(session.Notebook.Pages[0].Objects[0], obj), "追加の Redo で復元 (同一インスタンス)");

        // 変形 → Undo で操作前 → Redo で操作後 (完全一致)。
        var before = new ObjectTransform(1f, 2f, 100f, 50f, 0);
        var after = new ObjectTransform(33f, 44f, 220f, 110f, 270);
        session.TrySetObjectTransform(0, obj.Id, after);
        stack.Undo(document);
        check(obj.X == before.X && obj.Y == before.Y && obj.WidthPt == before.WidthPt &&
              obj.HeightPt == before.HeightPt && obj.RotationDeg == before.RotationDeg, "変形の Undo で操作前と完全一致");
        stack.Redo(document);
        check(obj.X == after.X && obj.WidthPt == after.WidthPt && obj.RotationDeg == after.RotationDeg,
            "変形の Redo で操作後と完全一致");

        // 同値変形は履歴に積まない。
        var depth = stack.CanUndo ? 1 : 0;
        session.TrySetObjectTransform(0, obj.Id, after);
        check((stack.CanUndo ? 1 : 0) == depth, "同値変形は履歴に積まれない");

        // Z 順変更 → Undo / Redo。
        session.TrySetObjectZ(0, obj.Id, 7);
        stack.Undo(document);
        check(obj.Z == 0, "Z 変更の Undo");
        stack.Redo(document);
        check(obj.Z == 7, "Z 変更の Redo");

        // 削除 → Undo で元の位置へ → Redo で削除。
        session.TryRemovePdfObject(0, obj.Id);
        stack.Undo(document);
        check(session.Notebook.Pages[0].Objects.Count == 1 &&
              session.Notebook.Pages[0].Objects.IndexOf(obj) == 0, "削除の Undo で元位置に復元");
        stack.Redo(document);
        check(session.Notebook.Pages[0].Objects.Count == 0, "削除の Redo");
    }

    internal static void MixedStrokeAndObjectHistory(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectUndo_MixedStrokeAndObjectHistory");
        var (session, notebook, stack, document) = CreateWithStrokes();
        var resourceId = session.Notebook.Resources[0].Id;

        // ストローク追加 (InkSession と同じ経路)。
        var stroke = AddStroke(notebook, document, 10f, 20f);
        stack.Push(new AddStrokeOperation(stroke));

        // オブジェクト追加 → ストローク削除 (消しゴム相当) → オブジェクト変形。
        var added = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 100f, 100f, 0));
        var obj = added.Object!;
        stack.Push(new RemoveStrokeOperation(stroke, notebook.Pages[0].Ink.Strokes.IndexOf(stroke)));
        notebook.Pages[0].Ink.Strokes.Remove(stroke);
        session.TrySetObjectTransform(0, obj.Id, new ObjectTransform(9f, 9f, 50f, 50f, 180));

        // Undo ×3: 変形解除 → ストローク復元 → オブジェクト削除。
        stack.Undo(document);
        check(obj.X == 0f && obj.RotationDeg == 0, "Undo1: 変形解除");
        stack.Undo(document);
        check(notebook.Pages[0].Ink.Strokes.Count == 1, "Undo2: ストローク復元");
        stack.Undo(document);
        check(notebook.Pages[0].Objects.Count == 0, "Undo3: オブジェクト削除");
        check(notebook.Pages[0].Ink.Strokes.Count == 1, "Undo 後もストロークは残る");

        // Redo ×3。
        stack.Redo(document);
        stack.Redo(document);
        stack.Redo(document);
        check(notebook.Pages[0].Objects.Count == 1 && obj.X == 9f, "Redo で最終状態に復元");
        check(notebook.Pages[0].Ink.Strokes.Count == 0, "Redo でストローク削除も復元");
    }
}
