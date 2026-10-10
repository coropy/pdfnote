using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ2: ストローク不変・ノート切替分離・履歴不整合の安全性。</summary>
internal static class PdfObjectIsolationTests
{
    internal static void StrokesInvariantAfterObjectOps(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectUndo_StrokesInvariant");
        var (session, notebook, _, document) = PdfObjectUndoTests.CreateWithStrokes();
        var resourceId = session.Notebook.Resources[0].Id;
        var stroke = PdfObjectUndoTests.AddStroke(notebook, document, 1f, 2f);
        stroke.Points.Add(new InkPoint(new Vector2(3f, 4f), 0.25f));

        var before = notebook.Pages[0].Ink.Strokes
            .Select(s => (Ref: s, Points: s.Points.Select(p => (p.Position.X, p.Position.Y, p.Pressure)).ToList()))
            .ToList();

        var added = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 50f, 50f, 0));
        session.TrySetObjectTransform(0, added.Object!.Id, new ObjectTransform(10f, 10f, 200f, 80f, 90));
        session.TrySetObjectZ(0, added.Object.Id, 3);
        session.TryRemovePdfObject(0, added.Object.Id);
        check(!session.TrySetObjectZ(0, added.Object.Id, 9), "対象不在の操作は false");

        check(notebook.Pages[0].Ink.Strokes.Count == before.Count, "ストローク本数不変");
        var after = notebook.Pages[0].Ink.Strokes
            .Select(s => (Ref: s, Points: s.Points.Select(p => (p.Position.X, p.Position.Y, p.Pressure)).ToList()))
            .ToList();
        var identical = before.Count == after.Count;
        for (var i = 0; identical && i < before.Count; i++)
        {
            identical = ReferenceEquals(before[i].Ref, after[i].Ref) &&
                        before[i].Points.Count == after[i].Points.Count &&
                        !before[i].Points.Zip(after[i].Points, (a, b) =>
                            Math.Abs(a.Item1 - b.Item1) > 0.0001 ||
                            Math.Abs(a.Item2 - b.Item2) > 0.0001 ||
                            Math.Abs(a.Item3 - b.Item3) > 0.0001).Any(diff => diff);
        }

        check(identical, "全オブジェクト操作後もストロークの参照・座標・筆圧・順序は不変");
    }

    internal static void NotebookSwitchIsolatesHistory(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectUndo_NotebookSwitchIsolation");
        var (session, notebook, stack, document) = PdfObjectUndoTests.CreateWithStrokes();
        var resourceId = session.Notebook.Resources[0].Id;
        var added = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 100f, 100f, 0));
        check(stack.CanUndo, "履歴に積まれている");

        // ノート切替 (AttachPages / ResetDocument が行う操作履歴クリア) を再現。
        stack.Clear();
        check(!stack.CanUndo && stack.Undo(document) is null, "切替後は履歴が空 (Undo なし)");

        // 新ノート (別インスタンス) は旧履歴の対象にならない。
        var otherNotebook = new Notebook(Guid.NewGuid(), "other");
        otherNotebook.Pages.Add(new NotebookPage(0, 595f, 842f, PageBackgroundKind.Plain));
        var stale = new AddPdfObjectOperation(notebook, 0, added.Object!);
        stale.Undo(document); // 旧ノートの操作を直接呼んでも…
        check(otherNotebook.Pages[0].Objects.Count == 0, "別ノートには作用しない (操作は生成時のノートに固定)");
        check(notebook.Pages[0].Objects.Count == 0, "旧ノート自体への操作は従来どおり機能");
    }

    internal static void StaleOperationIsSafe(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectUndo_StaleOperation");
        var (session, _, _, document) = PdfObjectUndoTests.CreateWithStrokes();
        var resourceId = session.Notebook.Resources[0].Id;
        var added = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 100f, 100f, 0));
        var obj = added.Object!;

        // 削除済みオブジェクトへの変形操作 (履歴不整合) でも例外を出さない。
        session.TryRemovePdfObject(0, obj.Id);
        var stale = new TransformPdfObjectOperation(
            obj,
            new ObjectTransform(0f, 0f, 100f, 100f, 0),
            new ObjectTransform(1f, 1f, 200f, 200f, 90));
        stale.Undo(document);
        stale.Redo(document);
        check(session.Notebook.Pages[0].Objects.Count == 0, "不整合操作はページ状態を変えず、クラッシュしない");
    }
}
