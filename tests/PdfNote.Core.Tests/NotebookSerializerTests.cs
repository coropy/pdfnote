using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>NotebookSerializer (v2 最小形式) の検証。</summary>
internal static class NotebookSerializerTests
{
    internal static void RoundTripPreservesPagesAndStrokes(Action<bool, string> check)
    {
        Console.WriteLine("Serializer_RoundTripPreservesPagesAndStrokes");
        var original = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Grid, pageCount: 3);
        original.SetPage(1);
        original.SetCurrentPageBackground(PageBackgroundKind.Grid);

        var stroke = new Stroke(1, InkTool.Pen, 2.5f, 20, 20, 20, 255, 999);
        stroke.Points.Add(new InkPoint(new Vector2(1.5f, 2.5f), 0.25f));
        stroke.Points.Add(new InkPoint(new Vector2(3.5f, 4.5f), 0.5f));
        stroke.Points.Add(new InkPoint(new Vector2(5.5f, 6.5f), 1.0f));
        original.Notebook.Pages[1].Ink.Strokes.Add(stroke);

        var file = NotebookSerializer.ToFile(original.Notebook);
        check(file.FormatVersion == 2, "formatVersion = 2");

        var path = Path.Combine(Path.GetTempPath(), $"pdfnote-test-{Guid.NewGuid():N}.nb.json");
        try
        {
            NotebookSerializer.Save(path, file);
            var restored = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(path));
            check(restored.Pages.Count == 3, "ページ数を復元");
            check(Math.Abs(restored.Pages[1].WidthPt - NotebookDefaults.A4WidthPt) < 0.001, "ページ幅復元");
            check(restored.Pages[1].Background == PageBackgroundKind.Grid, "背景復元");
            check(Math.Abs(restored.Pages[1].GridSpacingPt - NotebookDefaults.GridSpacingPt) < 0.01, "方眼間隔復元");

            var strokes = restored.Pages[1].Ink.Strokes;
            check(strokes.Count == 1, "ストローク数復元");
            var s = strokes[0];
            check(s.Points.Count == 3, "点数復元");
            check(Math.Abs(s.Points[0].Position.X - 1.5f) < 0.001 &&
                  Math.Abs(s.Points[0].Position.Y - 2.5f) < 0.001, "座標復元");
            check(Math.Abs(s.Points[1].Pressure - 0.5f) < 0.001, "筆圧復元");
            check(s.Tool == InkTool.Pen && Math.Abs(s.WidthPt - 2.5f) < 0.001, "ツール/幅復元");
            check(restored.Pages[0].Ink.Strokes.Count == 0, "他ページのストロークは混ざらない");
        }
        finally
        {
            Program.TryDelete(path);
            Program.TryDelete(path + ".bak");
        }
    }

    internal static void SecondSaveCreatesBackup(Action<bool, string> check)
    {
        Console.WriteLine("Serializer_SecondSaveCreatesBackup");
        var notebook = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain, pageCount: 2);

        var path = Path.Combine(Path.GetTempPath(), $"pdfnote-test-{Guid.NewGuid():N}.nb.json");
        try
        {
            NotebookSerializer.Save(path, NotebookSerializer.ToFile(notebook.Notebook));
            check(File.Exists(path), "1回目の保存");
            NotebookSerializer.Save(path, NotebookSerializer.ToFile(notebook.Notebook));
            check(File.Exists(path + ".bak"), "2回目の保存で .bak 退避");
        }
        finally
        {
            Program.TryDelete(path);
            Program.TryDelete(path + ".bak");
        }
    }

    internal static void DropsInvalidStrokesWithoutCrashing(Action<bool, string> check)
    {
        Console.WriteLine("Serializer_DropsInvalidStrokesWithoutCrashing");
        var file = new NotebookFileDto
        {
            FormatVersion = 2,
            Notebook = new NotebookInfoDto { Id = Guid.NewGuid().ToString("D"), Name = "t" },
            Pages =
            [
                new NotebookPageFileDto
                {
                    PageIndex = 0,
                    WidthPt = 100,
                    HeightPt = 100,
                    Background = new NotebookBackgroundDto { Kind = "Grid", GridSpacingPt = 20 },
                    Strokes =
                    [
                        new NoteStrokeDto { Tool = "Pen", Width = 2, ColorArgb = -1, Points = [1, 2, 0.5, 3, 4, 0.5] },
                        new NoteStrokeDto { Tool = "Pen", Width = 2, ColorArgb = -1, Points = [1, 2, double.NaN] },
                        new NoteStrokeDto { Tool = "Pen", Width = 0, ColorArgb = -1, Points = [1, 2, 0.5] },
                        new NoteStrokeDto { Tool = "Pen", Width = 2, ColorArgb = -1, Points = [1, 2] },
                    ],
                },
                new NotebookPageFileDto { PageIndex = -1, WidthPt = 100, HeightPt = 100 },
                new NotebookPageFileDto { PageIndex = 0, WidthPt = 0, HeightPt = 100 },
            ],
        };

        var restored = NotebookSerializer.ToNotebook(file);
        check(restored.Pages.Count == 1, "不正ページの破棄");
        check(restored.Pages[0].Ink.Strokes.Count == 1, "不正ストロークの破棄 (有効 1 本のみ)");
        check(restored.Pages[0].Background == PageBackgroundKind.Grid, "背景種別の復元");
    }

    internal static void RejectsUnknownVersion(Action<bool, string> check)
    {
        Console.WriteLine("Serializer_RejectsUnknownVersion");
        check(IsNoteLoadException("{\"formatVersion\":99}"), "不明バージョンの拒否");
        check(IsNoteLoadException("{}"), "バージョン欠損の拒否");
    }

    private static bool IsNoteLoadException(string json)
    {
        try
        {
            NotebookSerializer.ParseFile(json);
            return false;
        }
        catch (NoteLoadException)
        {
            return true;
        }
        catch (Exception)
        {
            // JSON 不正 (JsonException) は別系統。バージョン検証ではない。
            return false;
        }
    }
}
