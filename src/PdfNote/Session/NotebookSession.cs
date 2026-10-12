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
    private readonly Action<IInkOperation>? _pushOperation;
    private int _currentPage = -1;

    /// <summary>現在ページが変わった (呼び出しスレッド)。</summary>
    public event Action<int>? CurrentPageChanged;

    /// <summary>背景設定など、ストローク以外のページ内容が変わった (自動保存トリガー)。</summary>
    public event Action? ContentChanged;

    public NotebookSession(Notebook notebook, Action<IInkOperation>? pushOperation = null)
    {
        _notebook = notebook;
        _pushOperation = pushOperation;
    }

    /// <summary>白紙ノートを新規作成する。</summary>
    public static NotebookSession CreateNew(
        double widthPt,
        double heightPt,
        PageBackgroundKind background,
        int pageCount = NotebookDefaults.DefaultPageCount,
        Action<IInkOperation>? pushOperation = null)
    {
        var notebook = new Notebook(Guid.NewGuid(), "無題ノート");
        for (var i = 0; i < Math.Max(1, pageCount); i++)
        {
            notebook.Pages.Add(new NotebookPage(i, widthPt, heightPt, background));
        }

        return new NotebookSession(notebook, pushOperation);
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

    // ------------------------------------------------------------
    // PDF オブジェクト操作 (段階5 ステップ2)。
    // 変形はオブジェクトのデータのみを変更し、ストローク座標には一切触れない。
    // すべての操作は ContentChanged (自動保存トリガー) を発火し、
    // pushOperation (InkSession の操作履歴) に Undo 操作を積む。
    // ------------------------------------------------------------

    /// <summary>PdfObject 追加結果。ソフト上限超過などは無言で拒否せず呼び出し側に返す。</summary>
    public enum PdfObjectAddStatus
    {
        Added,
        PageIndexOutOfRange,
        PageLimitReached,
        UnknownResource,
        InvalidTransform,
    }

    /// <summary>PdfObject 追加の明示的な結果。</summary>
    public sealed class PdfObjectAddOutcome
    {
        public PdfObjectAddStatus Status { get; init; }

        public PdfObject? Object { get; init; }

        public static PdfObjectAddOutcome From(PdfObjectAddStatus status, PdfObject? obj = null) =>
            new() { Status = status, Object = obj };
    }

    /// <summary>現在ページに PDF オブジェクトを追加する。</summary>
    public PdfObjectAddOutcome AddPdfObject(
        Guid resourceId,
        int pdfPageIndex,
        ObjectTransform transform,
        Guid? objectId = null)
    {
        return AddPdfObject(_currentPage, resourceId, pdfPageIndex, transform, objectId);
    }

    /// <summary>
    /// 指定ページに PDF オブジェクトを追加する (ページ番号は Notebook.Pages のリスト位置)。
    /// ソフト上限 (<see cref="PageObjectLimits.MaxObjectsPerPage"/>) 超過時は
    /// <see cref="PdfObjectAddStatus.PageLimitReached"/> を返す (呼び出し側が警告と続行可否を判断する)。
    /// </summary>
    public PdfObjectAddOutcome AddPdfObject(
        int pageIndex,
        Guid resourceId,
        int pdfPageIndex,
        ObjectTransform transform,
        Guid? objectId = null,
        bool ignorePageLimit = false)
    {
        var page = PageAt(pageIndex);
        if (page is null)
        {
            return PdfObjectAddOutcome.From(PdfObjectAddStatus.PageIndexOutOfRange);
        }

        if (_notebook.Resources.All(r => r.Id != resourceId))
        {
            return PdfObjectAddOutcome.From(PdfObjectAddStatus.UnknownResource);
        }

        if (pdfPageIndex < 0)
        {
            return PdfObjectAddOutcome.From(PdfObjectAddStatus.PageIndexOutOfRange);
        }

        try
        {
            transform.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return PdfObjectAddOutcome.From(PdfObjectAddStatus.InvalidTransform);
        }

        // ソフト上限。超過時は無言で拒否せず、呼び出し側が警告と続行可否を判断する
        // (ignorePageLimit = true はユーザーの明示的な続行のみ)。
        if (page.Objects.Count >= PageObjectLimits.MaxObjectsPerPage && !ignorePageLimit)
        {
            return PdfObjectAddOutcome.From(PdfObjectAddStatus.PageLimitReached);
        }

        var pdfObject = new PdfObject(
            objectId ?? Guid.NewGuid(),
            resourceId,
            pdfPageIndex,
            transform.X, transform.Y, transform.WidthPt, transform.HeightPt,
            transform.RotationDeg,
            NextZ(page));
        page.Objects.Add(pdfObject);
        MarkObjectChanged(new AddPdfObjectOperation(_notebook, pageIndex, pdfObject));
        return PdfObjectAddOutcome.From(PdfObjectAddStatus.Added, pdfObject);
    }

    /// <summary>オブジェクトを削除する (Undo 可能)。対象が無ければ false。</summary>
    public bool TryRemovePdfObject(int pageIndex, Guid objectId)
    {
        var page = PageAt(pageIndex);
        if (page is null)
        {
            return false;
        }

        var index = page.Objects.FindIndex(o => o.Id == objectId);
        if (index < 0)
        {
            return false;
        }

        var removed = (PdfObject)page.Objects[index]; // 現状は PdfObject のみ (将来の種別は削除操作を拡張)。
        page.Objects.RemoveAt(index);
        MarkObjectChanged(new RemovePdfObjectOperation(_notebook, pageIndex, removed, index));
        return true;
    }

    /// <summary>オブジェクトを変形する (移動 / 拡大縮小 / 回転)。不正値は適用前に例外 (部分適用なし)。</summary>
    public bool TrySetObjectTransform(int pageIndex, Guid objectId, ObjectTransform transform)
    {
        transform.Validate();

        var page = PageAt(pageIndex);
        var obj = page?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        var before = ObjectTransform.Capture(obj);
        if (before == transform)
        {
            return true; // 同値変更は履歴に積まない。
        }

        transform.ApplyTo(obj);
        MarkObjectChanged(new TransformPdfObjectOperation(obj, before, transform));
        return true;
    }

    /// <summary>Z 順序を変更する。</summary>
    public bool TrySetObjectZ(int pageIndex, Guid objectId, int z)
    {
        var page = PageAt(pageIndex);
        var obj = page?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        if (obj.Z == z)
        {
            return true;
        }

        var before = obj.Z;
        obj.Z = z;
        MarkObjectChanged(new SetObjectZOperation(obj, before, z));
        return true;
    }

    // ------------------------------------------------------------
    // 選択ツール (段階5 ステップ5): ドラッグ中のプレビュー / コミット / キャンセル、Z 前後移動。
    // ------------------------------------------------------------

    /// <summary>
    /// ドラッグ中の変形を適用する (履歴に積まない)。
    /// ドラッグ終了時に <see cref="TryCommitObjectTransform"/> を 1 回呼ぶことで、
    /// ドラッグ 1 回 = 履歴 1 件になる。
    /// </summary>
    public bool TryPreviewObjectTransform(int pageIndex, Guid objectId, ObjectTransform transform)
    {
        transform.Validate();
        var obj = PageAt(pageIndex)?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        transform.ApplyTo(obj);
        Touch();
        ContentChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// ドラッグ終了: 開始時 (<paramref name="before"/>) から現在値への変形を 1 操作として履歴に積む。
    /// 同値の変形は履歴に積まない。
    /// </summary>
    public bool TryCommitObjectTransform(int pageIndex, Guid objectId, ObjectTransform before)
    {
        var obj = PageAt(pageIndex)?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        var after = ObjectTransform.Capture(obj);
        if (before == after)
        {
            return true;
        }

        Touch();
        ContentChanged?.Invoke();
        _pushOperation?.Invoke(new TransformPdfObjectOperation(obj, before, after));
        return true;
    }

    /// <summary>ドラッグのキャンセル: 開始時の変形へ戻す (履歴に積まない)。</summary>
    public bool TryRestoreObjectTransform(int pageIndex, Guid objectId, ObjectTransform before)
    {
        var obj = PageAt(pageIndex)?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        var after = ObjectTransform.Capture(obj);
        if (before == after)
        {
            return true;
        }

        before.Validate();
        before.ApplyTo(obj);
        Touch();
        ContentChanged?.Invoke();
        return true;
    }

    /// <summary>選択中オブジェクトを最前面へ (Z = 他の最大 + 1)。Undo 可能。</summary>
    public bool TryRaiseObjectZ(int pageIndex, Guid objectId)
    {
        var page = PageAt(pageIndex);
        var obj = page?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        var max = -1;
        foreach (var other in page!.Objects)
        {
            if (!ReferenceEquals(other, obj) && other.Z > max)
            {
                max = other.Z;
            }
        }

        return TrySetObjectZ(pageIndex, objectId, max + 1);
    }

    /// <summary>選択中オブジェクトを最背面へ (Z = 他の最小 − 1)。Undo 可能。</summary>
    public bool TryLowerObjectZ(int pageIndex, Guid objectId)
    {
        var page = PageAt(pageIndex);
        var obj = page?.Objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null)
        {
            return false;
        }

        var min = int.MaxValue;
        foreach (var other in page!.Objects)
        {
            if (!ReferenceEquals(other, obj) && other.Z < min)
            {
                min = other.Z;
            }
        }

        return TrySetObjectZ(pageIndex, objectId, min == int.MaxValue ? 0 : min - 1);
    }

    /// <summary>Z 順 → リスト順で並べた描画順 (段階5 手順4 で使用)。</summary>
    public IReadOnlyList<PageObject> GetObjectsInDrawOrder(int pageIndex)
    {
        var page = PageAt(pageIndex);
        if (page is null || page.Objects.Count == 0)
        {
            return Array.Empty<PageObject>();
        }

        return page.Objects
            .Select((obj, index) => (obj, index))
            .OrderBy(t => t.obj.Z)
            .ThenBy(t => t.index)
            .Select(t => t.obj)
            .ToList();
    }

    private NotebookPage? PageAt(int pageIndex) =>
        (uint)pageIndex < (uint)_notebook.Pages.Count ? _notebook.Pages[pageIndex] : null;

    private static int NextZ(NotebookPage page)
    {
        var max = -1;
        foreach (var obj in page.Objects)
        {
            if (obj.Z > max)
            {
                max = obj.Z;
            }
        }

        return max + 1;
    }

    private void MarkObjectChanged(IInkOperation operation)
    {
        Touch();
        ContentChanged?.Invoke();
        _pushOperation?.Invoke(operation);
    }
}
