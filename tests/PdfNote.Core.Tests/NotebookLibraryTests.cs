using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>NotebookLibrary (フォルダ保存 / 一覧 / 名前変更 / 削除 / 復元) の検証。</summary>
internal static class NotebookLibraryTests
{
    internal static void CreateListFindDelete(Action<bool, string> check)
    {
        Console.WriteLine("NotebookLibrary_CreateListFindDelete");
        var root = NewTempRoot();
        try
        {
            var nb1 = new Notebook(Guid.NewGuid(), "n");
            var nb2 = new Notebook(Guid.NewGuid(), "n");
            var f1 = NotebookLibrary.Create(root, "物理ノートA", nb1);
            var f2 = NotebookLibrary.Create(root, "物理/ノート:B?", nb2);

            check(Directory.Exists(Path.Combine(f1, "assets")), "assets/ を作成");
            check(Path.GetFileName(f1) == $"物理ノートA-{NotebookLibrary.ShortId(nb1.Id)}", "フォルダ名 (サニタイズ済み)");
            check(Path.GetFileName(f2) == $"物理_ノート_B_-{NotebookLibrary.ShortId(nb2.Id)}", "無効文字の置換");

            var list = NotebookLibrary.List(root);
            check(list.Count == 2, "一覧の件数");
            check(list[0].Name == "物理/ノート:B?", "最近更新順 (後作成が先頭)");
            check(list.All(s => s.Id is not null && !s.IsBroken), "Id 解決と正常判定");

            var found = NotebookLibrary.FindById(root, nb1.Id);
            check(found is not null && found.Value.Notebook.Id == nb1.Id, "Id による解決");
            check(found!.Value.Notebook.Name == "物理ノートA", "json 内の表示名");

            NotebookLibrary.Delete(f1);
            check(!Directory.Exists(f1) && NotebookLibrary.List(root).Count == 1, "削除");
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    internal static void RenameKeepsIdAndFallsBack(Action<bool, string> check)
    {
        Console.WriteLine("NotebookLibrary_RenameKeepsIdAndFallsBack");
        var root = NewTempRoot();
        try
        {
            var notebook = new Notebook(Guid.NewGuid(), "n");
            var folder = NotebookLibrary.Create(root, "旧名前", notebook);
            var id = notebook.Id;

            // 通常の名前変更: json 更新 + フォルダ名変更。
            var renamed = NotebookLibrary.Rename(folder, "新しい名前!");
            check(Path.GetFileName(renamed) == $"新しい名前!-{NotebookLibrary.ShortId(id)}", "フォルダ名変更");
            var listed = NotebookLibrary.List(root);
            check(listed.Count == 1 && listed[0].Name == "新しい名前!" && listed[0].Id == id,
                "名前変更後も Id は不変");

            // フォルダ名変更がロックで失敗する場合: json のみ更新されて続行する。
            var lockPath = Path.Combine(renamed, "assets", "lock.txt");
            File.WriteAllText(lockPath, "x");
            using (new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var after = NotebookLibrary.Rename(renamed, "さらに変更");
                check(after == renamed, "ロック時はフォルダ名を維持");
                var list2 = NotebookLibrary.List(root);
                check(list2[0].Name == "さらに変更" && list2[0].Id == id, "ロック時も json の Name/Id は一致");
            }
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    internal static void SaveRestoreRoundTripAcrossRestart(Action<bool, string> check)
    {
        Console.WriteLine("NotebookLibrary_SaveRestoreRoundTripAcrossRestart");
        var root = NewTempRoot();
        try
        {
            // 作成 → 編集 → 保存 (プロセス終了を想定)。
            var session = NotebookSession.CreateNew(
                NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Grid, pageCount: 4);
            session.SetPage(2);
            session.SetCurrentPageBackground(PageBackgroundKind.Plain);
            var stroke = new Stroke(2, InkTool.Pen, 3f, 10, 20, 30, 255, 42);
            stroke.Points.Add(new InkPoint(new Vector2(1f, 2f), 0.5f));
            stroke.Points.Add(new InkPoint(new Vector2(3f, 4f), 0.25f));
            session.Notebook.Pages[2].Ink.Strokes.Add(stroke);

            var folder = NotebookLibrary.Create(root, "再起動テスト", session.Notebook);
            var jsonPath = Path.Combine(folder, NotebookLibrary.NotebookFileName);
            NotebookSerializer.Save(jsonPath, session.CaptureSnapshot());

            // 再起動を想定: 新しい NotebookSession として復元する。
            var loaded = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(jsonPath));
            var restored = new NotebookSession(loaded);
            check(restored.PageCount == 4, "ページ数の復元");
            check(restored.Notebook.Id == session.Notebook.Id, "Id の復元");
            check(restored.Notebook.Pages[2].Background == PageBackgroundKind.Plain, "背景の復元");
            check(restored.Notebook.Pages[0].Background == PageBackgroundKind.Grid, "他ページの背景は不変");
            check(restored.Notebook.Pages[2].Ink.Strokes.Count == 1, "ストロークの復元");
            var s = restored.Notebook.Pages[2].Ink.Strokes[0];
            check(s.Points.Count == 2 &&
                  Math.Abs(s.Points[0].Position.X - 1f) < 0.001 &&
                  Math.Abs(s.Points[1].Pressure - 0.25f) < 0.001, "ストローク内容の一致");
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    internal static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pdfnote-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // クリーンアップ失敗は無視。
        }
    }
}
