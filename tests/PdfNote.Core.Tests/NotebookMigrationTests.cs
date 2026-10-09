using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>旧単一スロット移行 / v2前方互換 / テクスチャLRU の検証。</summary>
internal static class NotebookMigrationTests
{
    internal static void MigratesLegacySlotNonDestructively(Action<bool, string> check)
    {
        Console.WriteLine("NotebookSlotMigrator_MigratesLegacySlotNonDestructively");
        var root = NotebookLibraryTests.NewTempRoot();
        try
        {
            // 旧単一スロット形式のファイルを用意する。
            var legacy = NotebookSession.CreateNew(
                NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain, pageCount: 2);
            var stroke = new Stroke(0, InkTool.Pen, 2f, 0, 0, 0, 255, 7);
            stroke.Points.Add(new InkPoint(new Vector2(5f, 5f), 0.8f));
            legacy.Notebook.Pages[0].Ink.Strokes.Add(stroke);
            var legacyPath = Path.Combine(root, $"notebook-{NotebookLibrary.ShortId(legacy.Notebook.Id)}.nb.json");
            NotebookSerializer.Save(legacyPath, legacy.CaptureSnapshot());

            var count = NotebookSlotMigrator.MigratePending(root);
            check(count == 1, "移行成功数");

            // 旧ファイルは改名され内容が保たれる (削除されない)。
            check(!File.Exists(legacyPath), "旧パスからは消える");
            check(File.Exists(legacyPath + NotebookSlotMigrator.MigratedSuffix), ".migrated として保全");

            // 移行先フォルダから同じデータが読める。
            var summaries = NotebookLibrary.List(root);
            check(summaries.Count == 1 && summaries[0].PageCount == 2 && summaries[0].StrokeCount == 1,
                "移行先のページ数/ストローク数");
            check(NotebookLibrary.FindById(root, legacy.Notebook.Id) is not null, "Id で解決できる");

            // 2回目は何もしない。
            check(NotebookSlotMigrator.MigratePending(root) == 0, "再実行で冪等");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static void DtoIsForwardCompatible(Action<bool, string> check)
    {
        Console.WriteLine("NotebookSerializer_ForwardCompatible");
        // 未知のフィールド / resources 欠落でも読めること (v2 追加フィールドは任意)。
        var json = "{\"formatVersion\":2," +
                   "\"unknownFutureField\":{\"x\":1}," +
                   "\"notebook\":{\"id\":\"" + Guid.NewGuid().ToString("D") + "\",\"name\":\"互換\"}," +
                   "\"resources\":null," +
                   "\"pages\":[]}";
        var file = NotebookSerializer.ParseFile(json);
        check(file.Resources is null, "resources 欠落 (null) は許容");
        check(file.Pages is { Count: 0 }, "未知フィールドは無視される");

        var restored = NotebookSerializer.ToNotebook(file);
        check(restored.Pages.Count == 0 && restored.Name == "互換", "欠落リソースで復元可能");
    }

    internal static void TextureLruEviction(Action<bool, string> check)
    {
        Console.WriteLine("TextureLru_Eviction");
        var lru = new TextureLru(4);
        foreach (var page in new[] { 0, 1, 2, 3 })
        {
            lru.MarkUsed(page);
        }

        check(lru.Count == 4, "上限内は保持");

        // 新規ページ (4) を使用 → LRU 末尾 (0) を破棄候補とする。
        lru.MarkUsed(4);
        var evicted = lru.Evict(protectedPageIndex: 4);
        check(evicted.Count == 1 && evicted[0] == 0, "LRU 末尾を破棄");

        // 保護対象のみの状態では破棄しない。
        var lru2 = new TextureLru(1);
        lru2.MarkUsed(9);
        check(lru2.Evict(9).Count == 0, "保護対象のみは破棄しない");

        // 再使用で順序が更新される: {1,2} → 1 を再使用 → 3 を追加 → LRU 末尾の 2 が破棄される。
        var lru3 = new TextureLru(2);
        lru3.MarkUsed(1);
        lru3.MarkUsed(2);
        lru3.MarkUsed(1); // 1 を再使用 → LRU 末尾は 2
        lru3.MarkUsed(3); // 上限超過
        var evicted3 = lru3.Evict(3);
        check(evicted3.Count == 1 && evicted3[0] == 2, "再使用で LRU 順序更新");

        lru.Remove(4);
        check(lru.Count == 3, "Remove で管理外へ");
    }
}
