using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.View;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ5: 選択ツールのヒットテスト / 数学の検証。</summary>
internal static class PdfObjectSelectionTests
{
    /// <summary>テスト用: ページ 1 + リソース 1 のセッション (操作履歴つき)。</summary>
    internal static (NotebookSession Session, Notebook Notebook, InkUndoStack Stack, List<IInkOperation> Pushed)
        Create(Action<IInkOperation>? push = null)
    {
        var notebook = new Notebook(Guid.NewGuid(), "t");
        notebook.Resources.Add(new Resource(Guid.NewGuid(), "Pdf", "assets/x.pdf", 1, DateTime.UtcNow));
        notebook.Pages.Add(new NotebookPage(
            0, NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain));
        var stack = new InkUndoStack();
        var pushed = new List<IInkOperation>();
        var session = new NotebookSession(notebook, push ?? (op => { stack.Push(op); pushed.Add(op); }));
        session.SetPage(0);
        return (session, notebook, stack, pushed);
    }

    internal static void HitTestAllRotations(Action<bool, string> check)
    {
        Console.WriteLine("Selection_HitTestAllRotations");
        // 200×100 のバウンディング (100..300, 50..150)。
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            check(PdfObjectMath.HitTestObject(100f, 50f, 200f, 100f, rotation, new Vector2(200f, 100f)),
                $"{rotation}° 中心はヒット");
            check(PdfObjectMath.HitTestObject(100f, 50f, 200f, 100f, rotation, new Vector2(101f, 51f)),
                $"{rotation}° 左上隅付近はヒット");
            check(PdfObjectMath.HitTestObject(100f, 50f, 200f, 100f, rotation, new Vector2(299f, 149f)),
                $"{rotation}° 右下隅付近はヒット");
            check(!PdfObjectMath.HitTestObject(100f, 50f, 200f, 100f, rotation, new Vector2(301f, 100f)),
                $"{rotation}° 右外は非ヒット");
            check(!PdfObjectMath.HitTestObject(100f, 50f, 200f, 100f, rotation, new Vector2(200f, 151f)),
                $"{rotation}° 下外は非ヒット");
        }
    }

    internal static void ResizeMathClampsMinSize(Action<bool, string> check)
    {
        Console.WriteLine("Selection_ResizeMath");
        var t = new ObjectTransform(0f, 0f, 200f, 100f, 0);

        // 右下ハンドルを左上方向へ引き込む → 最小寸法でクランプ (反転しない)。
        var r = PdfObjectMath.ResizeFromCorner(
            t, (int)PdfObjectMath.PdfObjectResizeCorner.BottomRight, new Vector2(-50f, -80f), PageObjectLimits.MinObjectSizePt);
        check(r.WidthPt >= PageObjectLimits.MinObjectSizePt && r.HeightPt >= PageObjectLimits.MinObjectSizePt,
            "最小寸法を下回らない");
        check(Math.Abs(r.X - 0f) < 0.001 && Math.Abs(r.Y - 0f) < 0.001, "左上 anchor は固定");

        // 左上ハンドル → 右下 anchor は固定。
        var r2 = PdfObjectMath.ResizeFromCorner(
            t, (int)PdfObjectMath.PdfObjectResizeCorner.TopLeft, new Vector2(320f, 60f), PageObjectLimits.MinObjectSizePt);
        check(Math.Abs(r2.X + r2.WidthPt - 200f) < 0.001 && Math.Abs(r2.Y + r2.HeightPt - 100f) < 0.001,
            "右下 anchor は固定");

        // 移動は回転・寸法を変えない。
        var moved = PdfObjectMath.TranslateTransform(t, new Vector2(10f, -5f));
        check(Math.Abs(moved.X - 10f) < 0.001 && Math.Abs(moved.Y + 5f) < 0.001 &&
              Math.Abs(moved.WidthPt - 200f) < 0.001 && moved.RotationDeg == 0, "移動は位置のみ");
    }
}
