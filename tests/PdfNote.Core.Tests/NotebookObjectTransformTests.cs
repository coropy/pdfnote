using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ1: 不正オブジェクトの破棄と、変形がストロークに影響しないことの検証。</summary>
internal static class NotebookObjectTransformTests
{
    internal static void InvalidObjectsDroppedWithWarnings(Action<bool, string> check)
    {
        Console.WriteLine("InvalidObjectsDroppedWithWarnings");
        var resourceId = Guid.NewGuid();
        var file = new NotebookFileDto
        {
            FormatVersion = 2,
            Notebook = new NotebookInfoDto { Id = Guid.NewGuid().ToString("D"), Name = "t" },
            Resources = [new NotebookResourceDto { Id = resourceId.ToString("D"), Kind = "Pdf", FileName = "assets/x.pdf" }],
            Pages =
            [
                new NotebookPageFileDto
                {
                    PageIndex = 0,
                    WidthPt = 100,
                    HeightPt = 100,
                    Objects =
                    [
                        new NotebookObjectFileDto { Kind = "pdf", Id = Guid.NewGuid().ToString("D"), ResourceId = resourceId.ToString("D"), X = 1, Y = 2, WidthPt = 10, HeightPt = 10, RotationDeg = 0 },
                        new NotebookObjectFileDto { Kind = "image", Id = Guid.NewGuid().ToString("D"), ResourceId = resourceId.ToString("D"), X = 1, Y = 2, WidthPt = 10, HeightPt = 10 },
                        new NotebookObjectFileDto { Kind = "pdf", Id = "not-a-guid", ResourceId = resourceId.ToString("D"), X = 1, Y = 2, WidthPt = 10, HeightPt = 10 },
                        new NotebookObjectFileDto { Kind = "pdf", Id = Guid.NewGuid().ToString("D"), ResourceId = Guid.NewGuid().ToString("D"), X = 1, Y = 2, WidthPt = 10, HeightPt = 10 },
                        new NotebookObjectFileDto { Kind = "pdf", Id = Guid.NewGuid().ToString("D"), ResourceId = resourceId.ToString("D"), X = 1, Y = 2, WidthPt = 10, HeightPt = 10, RotationDeg = 45 },
                        new NotebookObjectFileDto { Kind = "pdf", Id = Guid.NewGuid().ToString("D"), ResourceId = resourceId.ToString("D"), X = 1, Y = 2, WidthPt = 0, HeightPt = 10 },
                        new NotebookObjectFileDto { Kind = "pdf", Id = Guid.NewGuid().ToString("D"), ResourceId = resourceId.ToString("D"), X = double.NaN, Y = 2, WidthPt = 10, HeightPt = 10 },
                        new NotebookObjectFileDto { Kind = "pdf", Id = Guid.NewGuid().ToString("D"), ResourceId = resourceId.ToString("D"), X = 1, Y = 2, WidthPt = 10, HeightPt = 10, PdfPageIndex = -1 },
                    ],
                },
            ],
        };

        var warnings = new List<string>();
        var restored = NotebookSerializer.ToNotebook(file, warnings);
        check(restored.Pages[0].Objects.Count == 1, "有効なオブジェクトのみ保持");
        check(warnings.Count == 7, $"破棄 7 件が警告に記録される (実際 {warnings.Count})");
    }

    internal static void ObjectTransformNeverTouchesStrokes(Action<bool, string> check)
    {
        Console.WriteLine("ObjectTransformNeverTouchesStrokes");
        var page = new NotebookPage(0, 595f, 842f, PageBackgroundKind.Plain);
        var stroke = new Stroke(0, InkTool.Pen, 2f, 0, 0, 0, 255, 1);
        stroke.Points.Add(new InkPoint(new Vector2(10f, 20f), 0.5f));
        stroke.Points.Add(new InkPoint(new Vector2(30f, 40f), 0.75f));
        page.Ink.Strokes.Add(stroke);

        var obj = new PdfObject(Guid.NewGuid(), Guid.NewGuid(), 0, 0f, 0f, 300f, 200f, 0, 0);
        page.Objects.Add(obj);
        var strokesBefore = page.Ink.Strokes
            .Select(s => (Ref: s, Points: s.Points.Select(p => (p.Position.X, p.Position.Y, p.Pressure)).ToList()))
            .ToList();

        // オブジェクトを移動 / 拡縮 / 回転 / Z 変更する。
        obj.X = 123f;
        obj.Y = 456f;
        obj.SetSize(400f, 250f);
        obj.SetRotation(180);
        obj.Z = 5;

        check(page.Ink.Strokes.Count == strokesBefore.Count, "ストローク本数不変");
        var identical = true;
        for (var i = 0; i < page.Ink.Strokes.Count; i++)
        {
            if (!ReferenceEquals(page.Ink.Strokes[i], strokesBefore[i].Ref))
            {
                identical = false;
            }

            var before = strokesBefore[i].Points;
            var after = page.Ink.Strokes[i].Points
                .Select(p => (p.Position.X, p.Position.Y, p.Pressure))
                .ToList();
            if (before.Count != after.Count)
            {
                identical = false;
                continue;
            }

            for (var j = 0; j < before.Count; j++)
            {
                if (Math.Abs(before[j].Item1 - after[j].Item1) > 0.0001 ||
                    Math.Abs(before[j].Item2 - after[j].Item2) > 0.0001 ||
                    Math.Abs(before[j].Item3 - after[j].Item3) > 0.0001)
                {
                    identical = false;
                }
            }
        }

        check(identical, "オブジェクト変形後もストローク座標は完全一致");
        check(obj.RotationDeg == 180 && obj.Z == 5 && Math.Abs(obj.X - 123f) < 0.001, "オブジェクト自身は変形されている");
    }
}
