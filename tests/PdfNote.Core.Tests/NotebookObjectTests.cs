using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ1: PDFオブジェクト / リソースの直列化・旧v2互換の検証。</summary>
internal static class NotebookObjectTests
{
    internal static void PdfObjectRoundTrip(Action<bool, string> check)
    {
        Console.WriteLine("PdfObject_RoundTrip");
        var notebook = new Notebook(Guid.NewGuid(), "オブジェクトテスト");
        var resource = new Resource(Guid.NewGuid(), "Pdf", $"assets/{Guid.NewGuid().ToString("N")[..8]}.pdf", 123456, DateTime.UtcNow);
        notebook.Resources.Add(resource);

        var page = new NotebookPage(0, NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain);
        var obj1 = new PdfObject(Guid.NewGuid(), resource.Id, 3, x: 10f, y: 20f, widthPt: 595f, heightPt: 842f, rotationDeg: 0, z: 0);
        var obj2 = new PdfObject(Guid.NewGuid(), resource.Id, 0, x: 300f, y: 100f, widthPt: 300f, heightPt: 200f, rotationDeg: 90, z: 1);
        page.Objects.Add(obj1);
        page.Objects.Add(obj2);

        var stroke = new Stroke(0, InkTool.Pen, 2f, 0, 0, 0, 255, 1);
        stroke.Points.Add(new InkPoint(new Vector2(50f, 60f), 0.5f));
        page.Ink.Strokes.Add(stroke);
        notebook.Pages.Add(page);

        var warnings = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"pdfnote-obj-{Guid.NewGuid():N}.nb.json");
        try
        {
            NotebookSerializer.Save(path, NotebookSerializer.ToFile(notebook));
            var restored = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(path), warnings);

            check(warnings.Count == 0, "警告なしで復元");
            check(restored.Resources.Count == 1 && restored.Resources[0].Id == resource.Id, "リソースの復元");
            check(restored.Resources[0].FileName == resource.FileName && restored.Resources[0].FileSize == 123456,
                "リソースの指紋");
            check(restored.Pages[0].Objects.Count == 2, "オブジェクト数の復元");

            var r1 = (PdfObject)restored.Pages[0].Objects[0];
            var r2 = (PdfObject)restored.Pages[0].Objects[1];
            check(r1.Id == obj1.Id && r1.ResourceId == resource.Id && r1.PdfPageIndex == 3, "オブジェクト1の参照");
            check(Math.Abs(r1.X - 10f) < 0.001 && Math.Abs(r1.Y - 20f) < 0.001 &&
                  Math.Abs(r1.WidthPt - 595f) < 0.001 && Math.Abs(r1.HeightPt - 842f) < 0.001, "オブジェクト1の矩形");
            check(r2.RotationDeg == 90 && r2.Z == 1, "オブジェクト2の回転/Z");
            check(restored.Pages[0].Ink.Strokes.Count == 1, "ストロークも併存");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(Path.GetDirectoryName(path)!);
        }
    }

    internal static void LegacyV2FileWithoutObjectsLoads(Action<bool, string> check)
    {
        Console.WriteLine("LegacyV2FileWithoutObjectsLoads");
        // 段階2〜4 形式 (objects / resources 欠落) をそのまま読めること。
        var legacy = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Grid, pageCount: 3);
        var file = new NotebookFileDto
        {
            FormatVersion = 2,
            Notebook = new NotebookInfoDto { Id = legacy.Notebook.Id.ToString("D"), Name = legacy.Notebook.Name },
            Pages =
            [
                new NotebookPageFileDto
                {
                    PageIndex = 0,
                    WidthPt = 100,
                    HeightPt = 100,
                    Strokes = [new NoteStrokeDto { Tool = "Pen", Width = 2, ColorArgb = -1, Points = [1, 2, 0.5] }],
                },
            ],
        };
        var warnings = new List<string>();
        var restored = NotebookSerializer.ToNotebook(file, warnings);

        check(restored.Pages[0].Objects.Count == 0, "旧形式はオブジェクト 0 で読める");
        check(restored.Resources.Count == 0, "旧形式はリソース 0 で読める");
        check(warnings.Count == 0, "欠落フィールドは警告を出さない");
        check(restored.Pages[0].Ink.Strokes.Count == 1, "ストロークは従来どおり復元");

        // 再保存しても形式が崩れない (往復の冪等性)。
        var roundTripped = NotebookSerializer.ToNotebook(NotebookSerializer.ToFile(restored));
        check(roundTripped.Pages[0].Objects.Count == 0 && roundTripped.Pages[0].Ink.Strokes.Count == 1,
            "旧形式 → 保存 → 読込 の往復");
    }
}
