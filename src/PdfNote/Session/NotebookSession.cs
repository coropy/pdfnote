using System;
using System.Collections.Generic;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Storage;

namespace PdfNote.Session;

/// <summary>
/// ノートブックの現在ページ管理とページ巡回 (段階2 最小実装)。
/// ストロークの入力・Undo/Redo・消しゴムは既存 InkSession が担い、
/// ページの手書きデータは NotebookPage.Ink (InkPage) を共有して接続する。
/// PDF のページ数には一切依存しない。
/// </summary>
public sealed class NotebookSession
{
    private readonly Notebook _notebook;
    private int _currentPage = -1;

    /// <summary>現在ページが変わった (呼び出しスレッド)。</summary>
    public event Action<int>? CurrentPageChanged;

    /// <summary>背景設定など、ストローク以外のページ内容が変わった (自動保存トリガー)。</summary>
    public event Action? ContentChanged;

    public NotebookSession(Notebook notebook)
    {
        _notebook = notebook;
    }

    /// <summary>白紙ノートを新規作成する。</summary>
    public static NotebookSession CreateNew(
        double widthPt,
        double heightPt,
        PageBackgroundKind background,
        int pageCount = NotebookDefaults.DefaultPageCount)
    {
        var notebook = new Notebook(Guid.NewGuid(), "無題ノート");
        for (var i = 0; i < Math.Max(1, pageCount); i++)
        {
            notebook.Pages.Add(new NotebookPage(i, widthPt, heightPt, background));
        }

        return new NotebookSession(notebook);
    }

    public Notebook Notebook => _notebook;

    public int PageCount => _notebook.Pages.Count;

    public int CurrentPage => _currentPage;

    public NotebookPage CurrentPageView =>
        _notebook.Pages[Math.Clamp(_currentPage, 0, _notebook.Pages.Count - 1)];

    public (double WidthPt, double HeightPt) GetPageSizePt(int pageIndex)
    {
        var page = _notebook.Pages[Math.Clamp(pageIndex, 0, _notebook.Pages.Count - 1)];
        return (page.WidthPt, page.HeightPt);
    }

    /// <summary>ページを切り替える (範囲外はクランプ)。</summary>
    public void SetPage(int pageIndex)
    {
        var page = Math.Clamp(pageIndex, 0, _notebook.Pages.Count - 1);
        if (page == _currentPage)
        {
            return;
        }

        _currentPage = page;
        CurrentPageChanged?.Invoke(page);
    }

    public void NextPage()
    {
        if (_currentPage + 1 < _notebook.Pages.Count)
        {
            SetPage(_currentPage + 1);
        }
    }

    public void PreviousPage()
    {
        if (_currentPage > 0)
        {
            SetPage(_currentPage - 1);
        }
    }

    public void FirstPage() => SetPage(0);

    public void LastPage() => SetPage(_notebook.Pages.Count - 1);

    /// <summary>
    /// 全ページの InkPage (InkSession への登録用)。
    /// NotebookPage.Ink と同一インスタンスを返すため、InkSession がここに書き込んだ
    /// ストロークは NotebookPage からそのまま参照できる。
    /// </summary>
    public IReadOnlyList<InkPage> GetInkPages()
    {
        var pages = new List<InkPage>(_notebook.Pages.Count);
        foreach (var page in _notebook.Pages)
        {
            pages.Add(page.Ink);
        }

        return pages;
    }

    /// <summary>現在ページの背景を変更する (自動保存トリガー)。</summary>
    public void SetCurrentPageBackground(PageBackgroundKind kind)
    {
        var page = CurrentPageView;
        if (page.Background == kind)
        {
            return;
        }

        page.Background = kind;
        Touch();
        ContentChanged?.Invoke();
    }

    /// <summary>更新日時を現在時刻にする (保存時に DTO へ反映)。</summary>
    public void Touch()
    {
        _notebook.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 現在のノートブック全体のスナップショット (保存用 DTO)。
    /// 呼び出しは UI スレッドを想定 (InkSession と同じ規約)。
    /// </summary>
    public NotebookFileDto CaptureSnapshot()
    {
        Touch();
        return NotebookSerializer.ToFile(_notebook);
    }
}
