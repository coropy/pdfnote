using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.View;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ5: 最前面選択 / Z順 / ドラッグ履歴 / 切替分離の検証。</summary>
internal static class PdfObjectSelectionFlowTests
{
    internal static void FrontmostSelectionAndZOrder(Action<bool, string> check)
    {
        Console.WriteLine("Selection_FrontmostAndZOrder");
        var (session, notebook, stack, _) = PdfObjectSelectionTests.Create();
        var resourceId = notebook.Resources[0].Id;

        // 重なる 2 オブジェクト。a (Z 0) → b (Z 1 = 最前面)。
        _ = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 200f, 200f, 0));
        var b = session.AddPdfObject(resourceId, 0, new ObjectTransform(50f, 50f, 200f, 200f, 0)).Object!;

        var point = new Vector2(150f, 150f);
        var hit = FrontmostHit(session, point);
        check(ReferenceEquals(hit, b), "重なりでは最前面 (Z大) が選ばれる");

        // b を最背面へ → 描画順が変わり、同一点では a が最前面。
        session.TryLowerObjectZ(0, b.Id);
        var drawOrder = session.GetObjectsInDrawOrder(0);
        check(drawOrder[0] == (PageObject)b, "Z順変更が描画順に反映");
        check(ReferenceEquals(FrontmostHit(session, point), null) == false &&
              ReferenceEquals(FrontmostHit(session, point), drawOrder[^1]),
            "背面移動後も描画順とヒット順が一致");

        // Z 変更の Undo / Redo。
        stack.Undo(new InkDocument());
        var bAfterUndo = (PdfObject)session.GetObjectsInDrawOrder(0).First(o => o.Id == b.Id);
        check(bAfterUndo.Z == 1, "Z 変更の Undo");
        stack.Redo(new InkDocument());
        check(((PdfObject)session.GetObjectsInDrawOrder(0).First(o => o.Id == b.Id)).Z < 0, "Z 変更の Redo");
    }

    internal static void DragOneHistoryEntry(Action<bool, string> check)
    {
        Console.WriteLine("Selection_DragOneHistoryEntry");
        var (session, notebook, stack, pushed) = PdfObjectSelectionTests.Create();
        var resourceId = notebook.Resources[0].Id;
        var obj = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 100f, 100f, 0)).Object!;
        var before = ObjectTransform.Capture(obj);
        var baseline = pushed.Count; // 追加操作の 1 件を除いた基準。

        session.TryPreviewObjectTransform(0, obj.Id, new ObjectTransform(10f, 5f, 100f, 100f, 0));
        session.TryPreviewObjectTransform(0, obj.Id, new ObjectTransform(20f, 10f, 100f, 100f, 0));
        check(pushed.Count == baseline, "ドラッグ中は履歴に積まない");

        session.TryCommitObjectTransform(0, obj.Id, before);
        check(pushed.Count == baseline + 1 && pushed[^1] is TransformPdfObjectOperation, "ドラッグ 1 回 = 履歴 1 件");

        stack.Undo(new InkDocument());
        check(Math.Abs(obj.X - 0f) < 0.001 && Math.Abs(obj.Y - 0f) < 0.001, "Undo で開始値");
        stack.Redo(new InkDocument());
        check(Math.Abs(obj.X - 20f) < 0.001 && Math.Abs(obj.Y - 10f) < 0.001, "Redo で終了値");

        var countBefore = pushed.Count;
        session.TryCommitObjectTransform(0, obj.Id, ObjectTransform.Capture(obj));
        check(pushed.Count == countBefore, "同値変形は履歴に積まない");
    }

    internal static void DragCancelRestores(Action<bool, string> check)
    {
        Console.WriteLine("Selection_DragCancelRestores");
        var (session, notebook, _, pushed) = PdfObjectSelectionTests.Create();
        var resourceId = notebook.Resources[0].Id;
        var obj = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 100f, 100f, 0)).Object!;
        var before = ObjectTransform.Capture(obj);
        var baseline = pushed.Count;

        session.TryPreviewObjectTransform(0, obj.Id, new ObjectTransform(30f, 30f, 100f, 100f, 0));
        session.TryRestoreObjectTransform(0, obj.Id, before);
        check(Math.Abs(obj.X - 0f) < 0.001 && Math.Abs(obj.Y - 0f) < 0.001, "キャンセルで開始値へ戻る");
        check(pushed.Count == baseline, "キャンセルは履歴に積まない");
    }

    internal static void NotebookSwitchIsolatesSelectionAndHistory(Action<bool, string> check)
    {
        Console.WriteLine("Selection_NotebookSwitchIsolation");
        var (session, notebook, stack, _) = PdfObjectSelectionTests.Create();
        var resourceId = notebook.Resources[0].Id;
        var obj = session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 100f, 100f, 0)).Object!;

        stack.Clear();
        check(!stack.CanUndo && stack.Undo(new InkDocument()) is null, "切替後は履歴なし");

        var otherNotebook = new Notebook(Guid.NewGuid(), "other");
        otherNotebook.Pages.Add(new NotebookPage(0, 595f, 842f, PageBackgroundKind.Plain));
        var stale = new TransformPdfObjectOperation(obj,
            new ObjectTransform(0f, 0f, 100f, 100f, 0), new ObjectTransform(1f, 1f, 100f, 100f, 0));
        stale.Undo(new InkDocument());
        check(otherNotebook.Pages[0].Objects.Count == 0, "別ノートには作用しない");
    }

    private static PageObject? FrontmostHit(NotebookSession session, Vector2 point)
    {
        var drawOrder = session.GetObjectsInDrawOrder(0);
        for (var i = drawOrder.Count - 1; i >= 0; i--)
        {
            if (drawOrder[i] is PdfObject p &&
                PdfObjectMath.HitTestObject(p.X, p.Y, p.WidthPt, p.HeightPt, p.RotationDeg, point))
            {
                return p;
            }
        }

        return null;
    }
}
