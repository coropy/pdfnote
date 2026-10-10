using PdfNote.Core;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ2: 取り込み失敗時の後始末と再オープン後のリソース解決。</summary>
internal static class PdfResourceFailureTests
{
    internal static void CopyFailureLeavesNothing(Action<bool, string> check)
    {
        Console.WriteLine("PdfResourceImport_CopyFailureLeavesNothing");
        var root = NotebookLibraryTests.NewTempRoot();
        try
        {
            var source = Path.Combine(root, "src.pdf");
            File.WriteAllBytes(source, new byte[128]);

            // 元ファイル不在 → FileNotFoundException、資産なし。
            var threw = false;
            try
            {
                NotebookLibrary.AddResource(root, Path.Combine(root, "missing.pdf"));
            }
            catch (FileNotFoundException)
            {
                threw = true;
            }

            check(threw && !Directory.Exists(Path.Combine(root, "assets")), "不在ソースは例外のみ");

            // コピー先がディレクトリで塞がれている → 例外、部分ファイルなし。
            var folder = NotebookLibrary.Create(root, "失敗テスト", new Notebook(Guid.NewGuid(), "n"));
            var blockedId = Guid.NewGuid();
            var blockedPath = Path.Combine(folder, "assets", $"{NotebookLibrary.ShortId(blockedId)}.pdf");
            Directory.CreateDirectory(blockedPath);

            threw = false;
            try
            {
                NotebookLibrary.AddResource(folder, source, resourceId: blockedId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                threw = true;
            }

            check(threw, "コピー失敗で例外");
            check(!File.Exists(blockedPath) && Directory.Exists(blockedPath),
                "部分コピーは残らない (塞いだディレクトリは影響なし)");

            // AddResource は成功時のみ Resource を返すため、例外処理すれば不完全な登録は残らない。
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static void RelativePathSurvivesReopen(Action<bool, string> check)
    {
        Console.WriteLine("PdfResourceImport_RelativePathSurvivesReopen");
        var root = NotebookLibraryTests.NewTempRoot();
        try
        {
            var source = Path.Combine(root, "doc.pdf");
            File.WriteAllBytes(source, new byte[512]);
            var notebook = new Notebook(Guid.NewGuid(), "再オープン");
            var folder = NotebookLibrary.Create(root, "再オープン", notebook);
            notebook.Resources.Add(NotebookLibrary.AddResource(folder, source));

            // 保存 → 「再オープン」(読み込み) → リソース解決。
            var jsonPath = Path.Combine(folder, NotebookLibrary.NotebookFileName);
            NotebookSerializer.Save(jsonPath, NotebookSerializer.ToFile(notebook));
            var loaded = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(jsonPath));

            check(loaded.Resources.Count == 1, "リソースの復元");
            var resolved = NotebookLibrary.FindResourceFile(folder, loaded.Resources[0]);
            check(resolved is not null && new FileInfo(resolved).Length == 512, "相対パスから実ファイルを解決");

            // 参照切れ (実体を移動) は null を返す (プレースホルダ表示の契機)。
            var movedAsset = Path.Combine(folder, "assets", "moved.pdf");
            File.Move(resolved!, movedAsset);
            check(NotebookLibrary.FindResourceFile(folder, loaded.Resources[0]) is null, "参照切れは null");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }
}
