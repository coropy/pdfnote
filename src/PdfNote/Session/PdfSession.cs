using System;
using System.Threading;
using System.Threading.Tasks;
using PdfNote.Core;

namespace PdfNote.Session;

/// <summary>
/// 現在ページ・必要解像度 (ズーム) の状態を管理し、キャッシュ / レンダラーキューへ要求を出す。
/// FrameReady はキャッシュヒット時は呼び出しスレッド、レンダリング後はレンダリングスレッドから発火する。
/// </summary>
public sealed class PdfSession : IDisposable
{
    /// <summary>飛行中要求と同一とみなす解像度の相対差。</summary>
    private const double InFlightDedupeTolerance = 0.02;

    /// <summary>レンダリング完了フレームを表示に使う解像度の相対誤差。</summary>
    private const double DeliverTolerance = 0.25;

    private readonly object _gate = new();
    private readonly PageBitmapCache _cache = new(maxEntries: 6, maxBytes: 192L * 1024 * 1024);

    private PdfDocument? _doc;
    private PdfRenderQueue? _queue;
    private int _currentPage = -1;
    private double _desiredPxPerPt = RenderScale.DefaultPxPerPt;
    private bool _inFlight;
    private int _inFlightPage = -1;
    private double _inFlightPxPerPt;

    /// <summary>表示すべきフレームが準備できた。キャッシュヒット時は呼び出しスレッド、それ以外はレンダリングスレッド。</summary>
    public event Action<RenderedFrame>? FrameReady;

    /// <summary>現在ページが変わった (呼び出しスレッド)。</summary>
    public event Action<int>? CurrentPageChanged;

    /// <summary>要求解像度が変わった。</summary>
    public event Action<double>? DesiredScaleChanged;

    /// <summary>バックグラウンドレンダリングが失敗した (レンダリングスレッド)。</summary>
    public event Action<Exception>? RenderFailed;

    public bool HasDocument
    {
        get { lock (_gate) { return _doc is not null; } }
    }

    public int PageCount
    {
        get { lock (_gate) { return _doc?.PageCount ?? 0; } }
    }

    public int CurrentPage
    {
        get { lock (_gate) { return _currentPage; } }
    }

    public (long Bytes, int Count) CacheStats => _cache.Stats;

    public (double WidthPt, double HeightPt) GetPageSizePt(int pageIndex)
    {
        lock (_gate)
        {
            var doc = _doc ?? throw new InvalidOperationException("PDFが開かれていません。");
            return doc.GetPageSizePt(pageIndex);
        }
    }

    /// <summary>PDF を開く。ページ寸法取得を含むため重い場合は呼び出し側で Task.Run を使うこと。</summary>
    public void Open(string filePath)
    {
        lock (_gate)
        {
            CloseCoreLocked();
            _doc = PdfDocument.Open(filePath);
            _queue = new PdfRenderQueue(_doc);
            _currentPage = 0;
            _desiredPxPerPt = RenderScale.DefaultPxPerPt;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            CloseCoreLocked();
        }
    }

    public void SetPage(int pageIndex)
    {
        int page;
        lock (_gate)
        {
            var doc = _doc;
            if (doc is null)
            {
                return;
            }

            page = Math.Clamp(pageIndex, 0, doc.PageCount - 1);
            _currentPage = page;
        }

        CurrentPageChanged?.Invoke(page);
        TryRender(force: false);
    }

    public void NextPage()
    {
        var page = CurrentPage;
        if (page + 1 < PageCount)
        {
            SetPage(page + 1);
        }
    }

    public void PreviousPage()
    {
        var page = CurrentPage;
        if (page > 0)
        {
            SetPage(page - 1);
        }
    }

    public void FirstPage()
    {
        if (HasDocument)
        {
            SetPage(0);
        }
    }

    public void LastPage()
    {
        var count = PageCount;
        if (count > 0)
        {
            SetPage(count - 1);
        }
    }

    /// <summary>必要解像度を設定する。force=true は同じ値でも再評価する (初回表示用)。</summary>
    public void SetDesiredScale(double pxPerPt, bool force = false)
    {
        var clamped = Math.Clamp(pxPerPt, RenderScale.MinPxPerPt, RenderScale.MaxPxPerPt);

        lock (_gate)
        {
            if (!force && Math.Abs(clamped - _desiredPxPerPt) < 0.001)
            {
                return;
            }

            _desiredPxPerPt = clamped;
        }

        DesiredScaleChanged?.Invoke(clamped);
        TryRender(force);
    }

    /// <summary>ズーム/パン操作が落ち着いたときに呼び出す (必要なら高解像度で再レンダリング)。</summary>
    public void OnZoomSettled() => TryRender(force: false);
    private void TryRender(bool force)
    {
        PdfDocument? doc;
        PdfRenderQueue? queue;
        int page;
        double target;

        lock (_gate)
        {
            doc = _doc;
            queue = _queue;
            if (doc is null || queue is null)
            {
                return;
            }

            page = _currentPage;
            if (page < 0)
            {
                return;
            }

            target = ClampToPixelBudgetLocked(page, _desiredPxPerPt);

            if (!force && _cache.TryGet(page, target, out var cached))
            {
                FrameReady?.Invoke(cached);
                return;
            }

            // 同一ページ・ほぼ同一解像度の要求が飛行中なら何もしない。
            if (_inFlight && _inFlightPage == page && Math.Abs(_inFlightPxPerPt / target - 1.0) < InFlightDedupeTolerance)
            {
                return;
            }

            _inFlight = true;
            _inFlightPage = page;
            _inFlightPxPerPt = target;
        }

        var task = queue.RenderAsync(page, target);
        _ = task.ContinueWith(
            OnRenderCompleted,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnRenderCompleted(Task<RenderedFrame> task)
    {
        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            var ex = task.Exception?.GetBaseException() ?? new Exception("ページのレンダリングに失敗しました。");
            lock (_gate)
            {
                _inFlight = false;
            }
            RenderFailed?.Invoke(ex);
            return;
        }

        var frame = task.Result;
        _cache.Add(frame);

        bool deliver;
        lock (_gate)
        {
            _inFlight = false;
            deliver = frame.PageIndex == _currentPage &&
                      Math.Abs(frame.PxPerPt / ClampToPixelBudgetLocked(_currentPage, _desiredPxPerPt) - 1.0) <= DeliverTolerance;
        }

        if (deliver)
        {
            FrameReady?.Invoke(frame);
        }

        // 飛行中に解像度/ページ要求が変わっていた場合はここで追いつく (dedupe により暴走しない)。
        TryRender(force: false);
    }

    /// <summary>1フレームあたりの総ピクセル数上限に合わせてレンダリング解像度をクランプする。</summary>
    private double ClampToPixelBudgetLocked(int page, double pxPerPt)
    {
        var doc = _doc!;
        var (widthPt, heightPt) = doc.GetPageSizePt(page);
        if (widthPt <= 0 || heightPt <= 0)
        {
            return pxPerPt;
        }

        var max = Math.Sqrt(RenderScale.MaxRenderPixels / (widthPt * heightPt));
        var scale = Math.Min(pxPerPt, max);
        return Math.Max(scale, Math.Min(RenderScale.MinPxPerPt, max));
    }

    private void CloseCoreLocked()
    {
        _queue?.Dispose();
        _queue = null;
        _doc?.Dispose();
        _doc = null;
        _cache.Clear();
        _currentPage = -1;
        _inFlight = false;
        _inFlightPage = -1;
    }

    public void Dispose() => Close();
}

