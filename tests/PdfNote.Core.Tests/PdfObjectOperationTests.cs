using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ2: PDF オブジェクト追加 / 上限 / 検証の検証。</summary>
internal static class PdfObjectOperationTests
{
    internal static void AddValidationAndSoftLimit(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectOperation_AddValidationAndSoftLimit");
        var (session, notebook, _) = Create();
        var resourceId = notebook.Resources[0].Id;
        var contentChanged = 0;
        session.ContentChanged += () => contentChanged++;

        var ok = session.AddPdfObject(resourceId, 2, new ObjectTransform(0f, 0f, 100f, 50f, 0));
        check(ok.Status == NotebookSession.PdfObjectAddStatus.Added && ok.Object!.Z == 0, "追加成功と Z=0");
        check(session.AddPdfObject(resourceId, 0, new ObjectTransform(1f, 1f, 10f, 10f, 90)).Object!.Z == 1,
            "Z は既存最大 + 1");

        // ソフト上限: 10 個目まで追加でき、11 個目は明示的な結果を返す。
        for (var i = session.Notebook.Pages[0].Objects.Count; i < PageObjectLimits.MaxObjectsPerPage; i++)
        {
            check(session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 10f, 10f, 0)).Status ==
                NotebookSession.PdfObjectAddStatus.Added, $"追加 {i + 1} 個目");
        }

        check(session.Notebook.Pages[0].Objects.Count == PageObjectLimits.MaxObjectsPerPage, "上限まで追加");
        check(session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 10f, 10f, 0)).Status ==
            NotebookSession.PdfObjectAddStatus.PageLimitReached, "超過は PageLimitReached を明示的に返す");
        check(session.Notebook.Pages[0].Objects.Count == PageObjectLimits.MaxObjectsPerPage, "無言追加はしない");

        // 検証。
        check(session.AddPdfObject(Guid.NewGuid(), 0, new ObjectTransform(0f, 0f, 10f, 10f, 0)).Status ==
            NotebookSession.PdfObjectAddStatus.UnknownResource, "未登録リソースは拒否");
        check(session.AddPdfObject(resourceId, -1, new ObjectTransform(0f, 0f, 10f, 10f, 0)).Status ==
            NotebookSession.PdfObjectAddStatus.PageIndexOutOfRange, "負の PDF ページ番号は拒否");
        check(session.AddPdfObject(resourceId, 0, new ObjectTransform(0f, 0f, 10f, 10f, 45)).Status ==
            NotebookSession.PdfObjectAddStatus.InvalidTransform, "不正回転は拒否");
        check(session.AddPdfObject(-5, resourceId, 0, new ObjectTransform(0f, 0f, 10f, 10f, 0)).Status ==
            NotebookSession.PdfObjectAddStatus.PageIndexOutOfRange, "ページ範囲外は拒否");
        check(contentChanged > 0, "操作が ContentChanged (dirty) を発火");

        // 描画順 (Z 順 → リスト順)。
        var drawOrder = session.GetObjectsInDrawOrder(0);
        check(drawOrder.Count == PageObjectLimits.MaxObjectsPerPage &&
              drawOrder[0].Z <= drawOrder[1].Z, "描画順は Z 昇順");
    }

    internal static void DirtyAndSaveRoundTrip(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectOperation_DirtyAndSaveRoundTrip");
        var (session, _, _) = Create(pageCount: 2);
        var resourceId = session.Notebook.Resources[0].Id;
        var contentChanged = 0;
        session.ContentChanged += () => contentChanged++;

        var before = contentChanged;
        var added = session.AddPdfObject(resourceId, 1, new ObjectTransform(5f, 6f, 200f, 300f, 90));
        check(contentChanged == before + 1, "追加で dirty 発火");

        session.TrySetObjectTransform(0, added.Object!.Id, new ObjectTransform(50f, 60f, 150f, 100f, 270));
        check(contentChanged == before + 2, "変形で dirty 発火");

        var path = Path.Combine(Path.GetTempPath(), $"pdfnote-op-{Guid.NewGuid():N}.nb.json");
        try
        {
            NotebookSerializer.Save(path, session.CaptureSnapshot());
            var loaded = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(path));
            var restored = (PdfObject)loaded.Pages[0].Objects[0];
            check(restored.Id == added.Object.Id, "Id の一致");
            check(Math.Abs(restored.X - 50f) < 0.001 && Math.Abs(restored.Y - 60f) < 0.001 &&
                  Math.Abs(restored.WidthPt - 150f) < 0.001 && Math.Abs(restored.HeightPt - 100f) < 0.001,
                "変形後の矩形が保存 / 復元で一致");
            check(restored.RotationDeg == 270 && restored.Z == 0, "回転 / Z の一致");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(Path.GetDirectoryName(path)!);
        }
    }

    /// <summary>テスト用: リソース1つ + ページ pageCount のセッションと操作履歴を作る。</summary>
    internal static (NotebookSession Session, Notebook Notebook, InkUndoStack Stack) Create(
        int pageCount = 1,
        Action<IInkOperation>? push = null)
    {
        var notebook = new Notebook(Guid.NewGuid(), "t");
        notebook.Resources.Add(new Resource(Guid.NewGuid(), "Pdf", "assets/x.pdf", 1, DateTime.UtcNow));
        for (var i = 0; i < pageCount; i++)
        {
            notebook.Pages.Add(new NotebookPage(
                i, NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain));
        }

        var stack = new InkUndoStack();
        var session = new NotebookSession(notebook, push ?? (op => stack.Push(op)));
        session.SetPage(0); // 現在ページを初期化 (テストは現在ページ経由で追加する)。
        return (session, notebook, stack);
    }
}
