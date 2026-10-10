using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfNote.Core;

namespace PdfNote.Session;

/// <summary>
/// ノート内 PDF リソースのページ数取得と非同期レンダリングを提供する (段階5 ステップ3)。
/// 既存の PdfDocument / PdfRenderQueue / PageBitmapCache / RenderScale を再利用し、
/// リソースごとに「ドキュメント + キュー + CPU キャッシュ」の束を最大 4 個まで開く。
///
/// スレッドモデル:
/// - オープン・寸法取得・破棄は Task.Run (UI スレッドで同期実行しない)。
/// - 描画は PdfRenderQueue の専用スレッド (latest-wins) で実行される。
/// - 本クラスの同期部分はロックと辞書操作のみ (軽量、UI スレッド呼び出し可)。
///
/// 陳腐化対策:
/// - 同一リソース・同一ページで新しい要求が来ると旧要求の待者はキャンセルされる。
/// - 描画が既に開始していた場合は完了まで待ち、結果は RequestId 不一致として
///   呼び出し側が破棄する (PDFium は途中キャンセル非対応のため「実処理は完了、結果は破棄」)。
/// </summary>
public sealed class PdfResourceRenderService : IDisposable
{
    /// <summary>同時に開けるリソース束の上限。</summary>
    public const int MaxOpenResources = PageObjectLimits.MaxResourceSessions;

    /// <summary>同一ページ要求の重複判定に使う解像度の相対差 (PdfSession と同じ 2%)。</summary>
    private const double DedupeScaleTolerance = 0.02;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, PdfResourceBundle> _bundles = new();
    private long _requestId;
    private bool _disposed;

    // ------------------------------------------------------------
    // オープン / 閉鎖。
    // ------------------------------------------------------------
    /// <summary>
    /// PDF リソースを開く (バックグラウンド)。既に開いていればそれを返す。
    /// 上限到達時は最終使用時刻が最も古いアイドル束を退避して再利用する。
    /// 退避できる束がない (全束が実行中) 場合は
    /// <see cref="PdfResourceServiceException"/>(CapacityExhausted)。
    /// ファイル不存在 / 破損 PDF は <see cref="PdfOpenException"/> として伝播する。
    /// </summary>
    public async Task<PdfResourceBundle> OpenAsync(
        Guid resourceId, string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        PdfResourceBundle reserved;
        lock (_gate)
        {
            if (_bundles.TryGetValue(resourceId, out var existing) && !existing.IsClosed)
            {
                existing.MarkUsed();
                return existing;
            }

            // 退避済み (IsClosed) のエントリは辞書から除いて再オープンする。
            _bundles.Remove(resourceId);
            EvictToMakeRoomLocked();
            reserved = new PdfResourceBundle(resourceId, filePath);
            _bundles[resourceId] = reserved; // スロット確保 (並行 Open による上限超過を防ぐ)。
        }

        try
        {
            var document = await Task.Run(
                () => PdfDocument.Open(filePath), cancellationToken).ConfigureAwait(false);
            reserved.Attach(document);
            return reserved;
        }
        catch
        {
            // 開放失敗: スロットを返却し、不完全な束を残さない。
            lock (_gate)
            {
                if (_bundles.TryGetValue(resourceId, out var current) && ReferenceEquals(current, reserved))
                {
                    _bundles.Remove(resourceId);
                }
            }

            throw;
        }
    }

    /// <summary>実ページ数を取得する (開いている場合。開放中 / 未開は null)。UI スレッドでも安全。</summary>
    public int? TryGetPageCount(Guid resourceId)
    {
        lock (_gate)
        {
            if (_bundles.TryGetValue(resourceId, out var bundle) && !bundle.Opening && !bundle.IsClosed)
            {
                return bundle.PageCount;
            }

            return null;
        }
    }

    /// <summary>
    /// ページ寸法を取得する (バックグラウンド)。未開は (NotOpen)、範囲外は (PageOutOfRange)。
    /// 実行中は退避されないため、寸法取得中の破棄競合は発生しない。
    /// </summary>
    public async Task<(double WidthPt, double HeightPt)> GetPageSizePtAsync(
        Guid resourceId, int pageIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var bundle = GetOpenBundleOrThrow(resourceId, pageIndex);
        bundle.MarkUsed();
        bundle.BeginAuxJob();
        try
        {
            return await Task.Run(
                () => bundle.Document!.GetPageSizePt(pageIndex), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bundle.EndAuxJob();
        }
    }

    // ------------------------------------------------------------
    // レンダリング。
    // ------------------------------------------------------------
    /// <summary>
    /// ページを指定解像度で非同期レンダリングする要求を発行する。
    /// 同期部分は検証と辞書操作のみ (UI スレッド呼び出し可)。
    /// 同一ページ・ほぼ同一解像度 (±2%) の実行中要求には統合され、
    /// 異なる解像度の新要求は旧要求を陳腐化させる (旧待者はキャンセル、
    /// 既に描画が始まっていた分は完了後に RequestId 不一致で破棄)。
    /// CPU キャッシュに許容解像度のフレームがあれば即完了 (FromCache)。
    /// </summary>
    /// <exception cref="ObjectDisposedException">サービス終了後。</exception>
    /// <exception cref="ArgumentOutOfRangeException">解像度が不正 (非有限 / 0 以下)。</exception>
    /// <exception cref="PdfResourceServiceException">未開放 / ページ範囲外。</exception>
    public PdfObjectRenderRequest Render(Guid resourceId, int pageIndex, double pxPerPt)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(pxPerPt) || pxPerPt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pxPerPt), "解像度 (px/pt) は正の有限値である必要があります。");
        }

        var bundle = GetOpenBundleOrThrow(resourceId, pageIndex);
        bundle.MarkUsed();

        var requestId = Interlocked.Increment(ref _requestId);
        var task = RenderCoreAsync(bundle, pageIndex, pxPerPt, requestId);
        return new PdfObjectRenderRequest(requestId, resourceId, pageIndex, pxPerPt, task);
    }

    private async Task<PdfObjectRenderResult> RenderCoreAsync(
        PdfResourceBundle bundle, int pageIndex, double pxPerPt, long requestId)
    {
        var tcs = new TaskCompletionSource<PdfObjectRenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new PdfRenderWaiter(requestId, tcs);
        PendingRender? mine = null;

        lock (bundle.Gate)
        {
            if (bundle.IsClosed || bundle.Closing)
            {
                tcs.TrySetCanceled();
            }
            else if (bundle.Cache!.TryGet(pageIndex, pxPerPt, out var cached))
            {
                // CPU キャッシュに許容解像度のフレームがある → 即完了 (再レンダリングなし)。
                tcs.SetResult(new PdfObjectRenderResult
                {
                    RequestId = requestId,
                    ResourceId = bundle.ResourceId,
                    PageIndex = pageIndex,
                    PxPerPt = pxPerPt,
                    Frame = cached,
                    FromCache = true,
                });
            }
            else if (bundle.Pending.TryGetValue(pageIndex, out var pending) && !pending.Superseded &&
                     Math.Abs(pending.PxPerPt / pxPerPt - 1.0) < DedupeScaleTolerance)
            {
                // 同一条件の実行中要求に統合する (キューへの重複投入なし)。
                pending.Waiters.Add(waiter);
                mine = pending;
            }
            else
            {
                // 陳腐化: 旧要求の待者をキャンセルして置き換える。
                // 描画中の旧処理は完了まで走るが、待者がいないため結果は破棄される。
                if (pending is not null)
                {
                    pending.Superseded = true;
                    foreach (var oldWaiter in pending.Waiters)
                    {
                        oldWaiter.Completion.TrySetCanceled();
                    }

                    bundle.Pending.Remove(pageIndex);
                }

                mine = new PendingRender(pageIndex, pxPerPt, waiter);
                bundle.Pending[pageIndex] = mine;
            }
        }

        // 実処理が必要なのは「スターター」(Waiters[0] = 自分) のみ。
        // キャッシュヒット / 終了済み / 陳腐化 / 統合された待者はスターターの完了を待つだけ。
        if (mine is null || !ReferenceEquals(mine.Waiters[0].Completion, tcs))
        {
            return await tcs.Task.ConfigureAwait(false);
        }

        try
        {
            // 寸法取得 (バックグラウンド)。PdfDocument が寸法をキャッシュするため再呼び出しは軽量。
            var (widthPt, heightPt) = await Task.Run(
                () => bundle.Document!.GetPageSizePt(pageIndex)).ConfigureAwait(false);

            // キュー投入はゲート内で行う (陳腐化との競合で新旧の順序が逆転しないようにする)。
            // 自分のエントリが既に新しい要求に置き換わっている場合は投げない
            // (新要求の RenderAsync をキャンセルしてしまうのを防ぐ)。
            Task<RenderedFrame>? queueTask;
            double scale;
            lock (bundle.Gate)
            {
                var current = bundle.Pending.TryGetValue(pageIndex, out var entry) ? entry : null;
                if (bundle.IsClosed || bundle.Closing ||
                    current is null || current.Superseded || !ReferenceEquals(current, mine))
                {
                    tcs.TrySetCanceled(); // 待者はキャンセル済み。
                    queueTask = null;
                    scale = 0;
                }
                else
                {
                    scale = ClampScale(widthPt, heightPt, pxPerPt);
                    queueTask = bundle.Queue!.RenderAsync(pageIndex, scale);
                    current.QueueTask = queueTask;
                }
            }

            if (queueTask is null)
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            var frame = await queueTask.ConfigureAwait(false);
            bundle.Cache!.Add(frame);

            CompletePending(bundle, mine, pageIndex, frame, scale, canceled: false, error: null);
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // より新しい要求に置き換えられた (latest-wins)。待者もキャンセル済み。
            CompletePending(bundle, mine, pageIndex, null, 0, canceled: true, error: null);
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 失敗は成功扱いにせず待者へ伝播する。
            CompletePending(bundle, mine, pageIndex, null, 0, canceled: false, error: ex);
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private void CompletePending(
        PdfResourceBundle bundle, PendingRender mine, int pageIndex,
        RenderedFrame? frame, double scale, bool canceled, Exception? error)
    {
        List<PdfRenderWaiter>? waiters = null;
        lock (bundle.Gate)
        {
            // 自分のエントリのみを片付ける (新要求のエントリを誤って取り消さない)。
            if (!bundle.Pending.TryGetValue(pageIndex, out var pending) || !ReferenceEquals(pending, mine))
            {
                return;
            }

            waiters = new List<PdfRenderWaiter>(mine.Waiters);
            bundle.Pending.Remove(pageIndex);
        }

        foreach (var waiter in waiters!)
        {
            if (canceled)
            {
                waiter.Completion.TrySetCanceled();
            }
            else if (error is not null)
            {
                waiter.Completion.TrySetException(error);
            }
            else
            {
                waiter.Completion.TrySetResult(new PdfObjectRenderResult
                {
                    RequestId = waiter.RequestId,
                    ResourceId = bundle.ResourceId,
                    PageIndex = pageIndex,
                    PxPerPt = scale,
                    Frame = frame!,
                    FromCache = false,
                });
            }
        }

        bundle.MaybeCloseWhenIdle();
    }

    // ------------------------------------------------------------
    // 上限 / 退避 / 破棄。
    // ------------------------------------------------------------
    private void EvictToMakeRoomLocked()
    {
        while (_bundles.Count >= MaxOpenResources)
        {
            PdfResourceBundle? victim = null;
            foreach (var pair in _bundles)
            {
                var candidate = pair.Value;
                if (candidate.Opening || candidate.IsClosed || candidate.Closing || candidate.IsBusy)
                {
                    continue; // 開放中 / 実行中 / 破棄済みは退避しない。
                }

                if (victim is null || candidate.LastUsedUtc < victim.LastUsedUtc)
                {
                    victim = candidate;
                }
            }

            if (victim is null)
            {
                throw new PdfResourceServiceException(
                    PdfResourceServiceError.CapacityExhausted,
                    $"同時に開ける PDF リソースは {MaxOpenResources} 個までです (実行中のため退避できません)。");
            }

            _bundles.Remove(victim.ResourceId);
            victim.Dispose();
        }
    }

    private PdfResourceBundle GetOpenBundleOrThrow(Guid resourceId, int pageIndex)
    {
        lock (_gate)
        {
            if (!_bundles.TryGetValue(resourceId, out var bundle))
            {
                throw new PdfResourceServiceException(
                    PdfResourceServiceError.NotOpen,
                    "PDF リソースが開かれていません (先に OpenAsync を呼び出してください)。");
            }

            if (bundle.Opening)
            {
                throw new PdfResourceServiceException(
                    PdfResourceServiceError.NotOpen,
                    "PDF リソースを開放中です (OpenAsync の完了待ちが必要です)。");
            }

            if (bundle.IsClosed || bundle.Closing)
            {
                throw new PdfResourceServiceException(
                    PdfResourceServiceError.NotOpen,
                    "PDF リソースは退避または終了処理中です。");
            }

            if (!bundle.IsValidPage(pageIndex))
            {
                throw new PdfResourceServiceException(
                    PdfResourceServiceError.PageOutOfRange,
                    $"ページ番号 {pageIndex} は PDF の範囲外です (ページ数 {bundle.PageCount})。");
            }

            return bundle;
        }
    }

    /// <summary>出力サイズ上限クランプ (既存 PdfSession / RenderScale と同じ規約)。</summary>
    internal static double ClampScale(double widthPt, double heightPt, double pxPerPt)
    {
        if (widthPt <= 0 || heightPt <= 0)
        {
            return pxPerPt;
        }

        var maxByPixels = Math.Sqrt(RenderScale.MaxRenderPixels / (widthPt * heightPt));
        var maxByDimension = RenderScale.MaxRenderDimensionPx / Math.Max(widthPt, heightPt);
        var max = Math.Min(maxByPixels, maxByDimension);
        return Math.Max(Math.Min(pxPerPt, max), Math.Min(RenderScale.MinPxPerPt, max));
    }

    public void Dispose()
    {
        List<PdfResourceBundle> bundles;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bundles = new List<PdfResourceBundle>(_bundles.Values);
        }

        foreach (var bundle in bundles)
        {
            var busy = false;
            lock (bundle.Gate)
            {
                busy = bundle.IsBusy;
                if (busy)
                {
                    // 終了処理中: 実行中処理の完了後に解放される (CompletePending / EndAuxJob)。
                    bundle.Closing = true;
                    foreach (var pending in bundle.Pending.Values)
                    {
                        pending.Superseded = true;
                        foreach (var waiter in pending.Waiters)
                        {
                            waiter.Completion.TrySetCanceled();
                        }
                    }
                }
            }

            if (!busy)
            {
                bundle.Dispose();
            }

            // 実行中の束の Queue / Document には触れない (レンダリングと競合する)。
            // CompletePending / EndAuxJob がアイドルになった時点で安全に解放する。
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfResourceRenderService));
        }
    }
}
