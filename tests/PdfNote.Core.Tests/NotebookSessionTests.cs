using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>NotebookSession (ページ管理 / Ink 共有) の検証。</summary>
internal static class NotebookSessionTests
{
    internal static void Basics(Action<bool, string> check)
    {
        Console.WriteLine("NotebookSession_Basics");
        var session = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain);

        check(session.PageCount == NotebookDefaults.DefaultPageCount, "既定ページ数");
        check(session.CurrentPage == -1, "初期ページは未設定");

        var pageChanged = 0;
        int? changedTo = null;
        session.CurrentPageChanged += page => { pageChanged++; changedTo = page; };
        session.SetPage(0);
        check(session.CurrentPage == 0 && pageChanged == 1 && changedTo == 0, "SetPage(0) でイベント発火");

        session.NextPage();
        check(session.CurrentPage == 1, "NextPage");
        session.PreviousPage();
        check(session.CurrentPage == 0, "PreviousPage");
        session.PreviousPage();
        check(session.CurrentPage == 0, "先端で PreviousPage はクランプ");
        session.LastPage();
        check(session.CurrentPage == session.PageCount - 1, "LastPage");
        session.NextPage();
        check(session.CurrentPage == session.PageCount - 1, "末端で NextPage はクランプ");

        var (w, h) = session.GetPageSizePt(0);
        check(Math.Abs(w - NotebookDefaults.A4WidthPt) < 0.001 &&
              Math.Abs(h - NotebookDefaults.A4HeightPt) < 0.001, "ページ寸法 (A4 pt)");

        var contentChanged = 0;
        session.ContentChanged += () => contentChanged++;
        session.SetCurrentPageBackground(PageBackgroundKind.Grid);
        check(session.CurrentPageView.Background == PageBackgroundKind.Grid && contentChanged == 1,
            "背景変更 + ContentChanged");
        session.SetCurrentPageBackground(PageBackgroundKind.Grid);
        check(contentChanged == 1, "同値背景変更は発火しない");

        check(session.GetInkPages().Count == session.PageCount, "GetInkPages の数");
        check(session.GetInkPages()[2].PageIndex == 2, "InkPage.PageIndex はリスト位置と一致");
    }

    /// <summary>
    /// InkSession (UI 依存のため直接は実行不可) と同じ経路を
    /// InkDocument.AttachPage 経由で再現し、NotebookPage との共有を確認する。
    /// </summary>
    internal static void SharedInkPageSurvivesDocumentAttach(Action<bool, string> check)
    {
        Console.WriteLine("NotebookSession_SharedInkPageSurvivesDocumentAttach");
        var session = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt, NotebookDefaults.A4HeightPt, PageBackgroundKind.Plain, pageCount: 3);

        var document = new InkDocument();
        foreach (var inkPage in session.GetInkPages())
        {
            document.AttachPage(inkPage.PageIndex, inkPage);
        }

        // InkSession と同じ操作: ストローク追加 → NotebookPage から見えること。
        var stroke = new Stroke(1, InkTool.Pen, 2f, 20, 20, 20, 255, 123);
        stroke.Points.Add(new InkPoint(new Vector2(10f, 20f), 0.5f));
        stroke.Points.Add(new InkPoint(new Vector2(30f, 40f), 0.75f));
        document.GetOrCreatePage(stroke.PageIndex).Strokes.Add(stroke);

        check(session.Notebook.Pages[1].Ink.Strokes.Count == 1, "InkDocument 追加が NotebookPage から見える");
        check(ReferenceEquals(document.TryGetPage(1), session.Notebook.Pages[1].Ink), "InkPage は同一インスタンス");

        // 消しゴム相当 (Remove) も共有先に反映される。
        document.TryGetPage(1)!.Strokes.Remove(stroke);
        check(session.Notebook.Pages[1].Ink.Strokes.Count == 0, "削除も NotebookPage に反映");
    }
}
