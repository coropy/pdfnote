using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Windows.Graphics.DirectX;
using Windows.UI;
using PdfNote.Core;
using PdfNote.Session;
using PdfNote.Storage;

namespace PdfNote.View;

/// <summary>
/// ノートブックページへの PDF オブジェクト合成層 (段階5 ステップ4)。
/// PdfCanvas.OnDraw が 背景 → 本層 (Z 順) → Ink の順に呼び出す。
///
/// - テクスチャは PDF 専用キャッシュ (PdfObjectTextureCache, InkRenderer / TextureLru とは独立)。
/// - CanvasBitmap の生成 / 破棄はすべて UI スレッド (Detach / 完了通知は DispatcherQueue 経由)。
/// - ペン入力経路には一切関与しない (ストロークはページ座標のまま、PDF 変形の影響を受けない)。
/// - 陳腐化対策: オブジェクトごとの最新 RequestId を保持し、古い完了結果は反映しない。
/// - リソース欠落 / 破損 / 範囲外ページはプレースホルダ表示 + 再試行間隔管理 (毎フレーム要求しない)。
/// </summary>
public sealed class PdfObjectLayer
{
    private static readonly Color PlaceholderFill = Color.FromArgb(255, 238, 238, 238);
    private static readonly Color PlaceholderBorder = Color.FromArgb(255, 170, 170, 170);

    /// <summary>オープン / ページレンダ失敗後の再試行間隔 (毎フレームの再要求を防ぐ)。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly PdfResourceRenderService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly PdfObjectTextureCache _textures = new();
    private readonly Dictionary<Guid, ResourceState> _resourceStates = new();

    /// <summary>オブジェクト → 飛行中の要求。null は寸法取得中の予約。</summary>
    private readonly Dictionary<Guid, PdfObjectRenderRequest?> _pending = new();

    /// <summary>オブジェクト → 採用すべき最新 RequestId (古い完了結果の破棄判定用)。</summary>
    private readonly Dictionary<Guid, long> _latestRequestId = new();

    private readonly Action _invalidate;

    /// <summary>選択中のオブジェクト ID (選択ツールが設定。選択枠描画用。保存データには含めない)。</summary>
    public Guid? SelectedObjectId { get; set; }

    /// <summary>最新要求と不一致で破棄した古い結果の累計 (F9 計測用)。</summary>
    public long StaleDiscardedCount { get; private set; }

    /// <summary>現在、陳腐化テクスチャを拡縮表示しているか (暫定画質の内部区別。ズーム確定で解消)。</summary>
    public bool DisplayingStale { get; private set; }

    private NotebookSession? _session;
    private Action? _contentChangedSubscription;
    private string? _notebookFolder;
    private int _generation;

    /// <summary>リソースごとの状態 (UI スレッドで読み書き。Dims のみバックグラウンド書込みがあるためロック)。</summary>
    private sealed class ResourceState
    {
        public readonly object Gate = new();
        public bool Missing;
        public bool OpenFailed;
        public bool OpenInProgress;
        public DateTime RetryAfterUtc = DateTime.MinValue;
        public int PageCount;
        public bool PageCountKnown;

        /// <summary>PDF ページ番号 → 寸法 (pt)。バックグラウンドで書き込まれるため Gate で保護。</summary>
        public readonly Dictionary<int, (double WidthPt, double HeightPt)> Dims = new();

        /// <summary>ページレンダ失敗の再試行時刻。</summary>
        public readonly Dictionary<int, DateTime> PageRetryAfterUtc = new();

        /// <summary>開いた後に実ページ数範囲外だったオブジェクト数 (削除せず警告付きプレースホルダで保持)。</summary>
        public int InvalidObjectCount;

        /// <summary>警告ログを 1 回だけ出すためのフラグ (毎フレーム大量出力の防止)。</summary>
        public bool LoggedOpenFailure;
        public bool WarnedInvalidObjects;
    }

    public PdfObjectLayer(PdfResourceRenderService service, DispatcherQueue dispatcher, Action invalidate)
    {
        _service = service;
        _dispatcher = dispatcher;
        _invalidate = invalidate;
    }

    public void Attach(NotebookSession session, string notebookFolder)
    {
        Detach();
        _session = session;
        _notebookFolder = notebookFolder;
        _generation++;
        _contentChangedSubscription = () =>
        {
            if (ReferenceEquals(_session, session))
            {
                OnContentChanged();
            }
        };
        session.ContentChanged += _contentChangedSubscription;
        _invalidate();
    }

    public void Detach()
    {
        // UI スレッドから呼ぶこと (CanvasBitmap の破棄を含む)。
        if (_session is not null && _contentChangedSubscription is not null)
        {
            _session.ContentChanged -= _contentChangedSubscription;
        }

        _session = null;
        _contentChangedSubscription = null;
        _notebookFolder = null;
        _generation++; // 進行中の非同期結果をすべて破棄させる。
        lock (_pending)
        {
            _pending.Clear();
            _latestRequestId.Clear();
        }

        foreach (var (_, bitmap) in _textures.Clear())
        {
            bitmap.Dispose();
        }

        _resourceStates.Clear();
    }

    /// <summary>ズーム確定後 (350ms デバウンス) の再レンダリング要求。</summary>
    public void OnZoomSettled() => OnContentChanged();

    /// <summary>Draw で観測した最新の表示スケール (再レンダリング要求の計算用)。</summary>
    private double _lastDisplayPxPerPt = RenderScale.DefaultPxPerPt;

    /// <summary>Draw で観測した最新のビューポート (ページpt座標)。画面外への無制限要求を防ぐ。</summary>
    private (double X, double Y, double Width, double Height) _lastViewport;

    /// <summary>
    /// モデル変更後の再描画と、必要な場合のみの再レンダリング要求。
    /// ペンストロークの追加では PDF は陳腐化しないため、要求は dedupe されて発火しない。
    /// </summary>
    public void OnContentChanged()
    {
        _invalidate();
        var session = _session;
        if (session is null)
        {
            return;
        }

        var page = session.CurrentPage;
        if ((uint)page >= (uint)session.PageCount)
        {
            return;
        }

        foreach (var obj in session.GetObjectsInDrawOrder(page))
        {
            if (obj is not PdfObject pdf)
            {
                continue;
            }

            // 画面外は要求しない (スクロールで視界に入った時点で Draw が要求する)。
            if (!IsVisible(pdf))
            {
                continue;
            }

            var decision = Evaluate(pdf, _lastDisplayPxPerPt, out _, out var content, out _, out var state);
            if (decision is PdfObjectDrawAction.RequestTexture or PdfObjectDrawAction.ReuseStaleTexture)
            {
                BeginRender(state!, pdf, content, _lastDisplayPxPerPt);
            }
        }
    }

    private bool IsVisible(PdfObject pdf)
    {
        var v = _lastViewport;
        return PdfObjectMath.IntersectsViewport(
            pdf.X, pdf.Y, pdf.WidthPt, pdf.HeightPt, v.X, v.Y, v.Width, v.Height, marginPt: 64);
    }

    /// <summary>現在ページの PDF オブジェクト数 (F9 計測用)。</summary>
    public int ObjectCountOnCurrentPage
    {
        get
        {
            var session = _session;
            if (session is null)
            {
                return 0;
            }

            var page = session.CurrentPage;
            if ((uint)page >= (uint)session.PageCount)
            {
                return 0;
            }

            return session.GetObjectsInDrawOrder(page).Count;
        }
    }

    /// <summary>保留中のレンダリング要求数 (F9 計測用)。</summary>
    public int PendingCount
    {
        get
        {
            lock (_pending)
            {
                var count = 0;
                foreach (var pending in _pending.Values)
                {
                    if (pending is not null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>PdfCanvas.OnDraw から呼ばれる (背景の後、Ink の前)。</summary>
    /// <param name="visiblePageRect">ビューポートのページpt座標 (X, Y, Width, Height)。画面外への要求を防ぐ。</param>
    public void Draw(
        CanvasDrawingSession session, Matrix3x2 pageTransform, double displayPxPerPt,
        (double X, double Y, double Width, double Height) visiblePageRect)
    {
        _lastDisplayPxPerPt = displayPxPerPt;
        _lastViewport = visiblePageRect;
        DisplayingStale = false;
        var s = _session;
        if (s is null)
        {
            return;
        }

        var page = s.CurrentPage;
        if ((uint)page >= (uint)s.PageCount)
        {
            return;
        }

        var drawOrder = s.GetObjectsInDrawOrder(page);
        foreach (var obj in drawOrder)
        {
            if (obj is not PdfObject pdf)
            {
                continue;
            }

            // 画面外は描画も要求もしない (スクロールで視界に入った時点で処理される)。
            if (!IsVisible(pdf))
            {
                continue;
            }

            try
            {
                DrawObject(session, pageTransform, displayPxPerPt, pdf);
            }
            catch
            {
                // 1 オブジェクトの失敗で描画ループ全体を停止させない。
            }
        }

        // 選択枠 (PDF オブジェクトの上、Ink の下。保存データやキャッシュには含めない)。
        if (SelectedObjectId is { } selectedId)
        {
            if (drawOrder.FirstOrDefault(o => o.Id == selectedId) is PdfObject selected && IsVisible(selected))
            {
                DrawSelectionFrame(session, pageTransform, displayPxPerPt, selected);
            }
        }
    }

    /// <summary>
    /// 選択枠と4隅ハンドルを描画する。破線・ハンドルは画面固定サイズ (÷ 表示スケール)。
    /// </summary>
    private static void DrawSelectionFrame(
        CanvasDrawingSession session, Matrix3x2 pageTransform, double displayPxPerPt, PdfObject pdf)
    {
        var blue = Color.FromArgb(255, 0, 120, 215);
        var scale = (float)Math.Max(displayPxPerPt, 0.1);
        var strokeWidth = 1.5f / scale;
        var handleHalf = 4f / scale; // 8px のハンドル。

        var previous = session.Transform;
        session.Transform = pageTransform;
        var dashStyle = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using (dashStyle)
        {
            session.DrawRectangle(pdf.X, pdf.Y, pdf.WidthPt, pdf.HeightPt, blue, strokeWidth, dashStyle);
        }

        var corners = new[]
        {
            new Vector2(pdf.X, pdf.Y),
            new Vector2(pdf.X + pdf.WidthPt, pdf.Y),
            new Vector2(pdf.X + pdf.WidthPt, pdf.Y + pdf.HeightPt),
            new Vector2(pdf.X, pdf.Y + pdf.HeightPt),
        };
        foreach (var corner in corners)
        {
            session.FillRectangle(corner.X - handleHalf, corner.Y - handleHalf, handleHalf * 2f, handleHalf * 2f, Colors.White);
            session.DrawRectangle(corner.X - handleHalf, corner.Y - handleHalf, handleHalf * 2f, handleHalf * 2f, blue, strokeWidth);
        }

        session.Transform = previous;
    }

    /// <summary>現在の推定 GPU テクスチャ使用量 (F9 表示など将来用)。</summary>
    public (int Count, long EstimatedBytes) TextureStats => _textures.Stats;

    // ------------------------------------------------------------
    // 描画。
    // ------------------------------------------------------------
    private void DrawObject(
        CanvasDrawingSession session, Matrix3x2 pageTransform, double displayPxPerPt, PdfObject pdf)
    {
        var decision = Evaluate(pdf, displayPxPerPt, out var texture, out var content, out var pdfDims, out var state);

        switch (decision)
        {
            case PdfObjectDrawAction.DrawTexture:
                DrawTexture(session, pageTransform, texture!.Value.Bitmap, texture.Value.Key.PxPerPt, content, pdfDims, pdf);
                break;

            case PdfObjectDrawAction.ReuseStaleTexture:
                // ズーム中は旧テクスチャを拡縮表示して空白化を避ける。
                // 再レンダリング要求はズーム確定 (OnZoomSettled → OnContentChanged) で出す。
                DisplayingStale = true;
                DrawTexture(session, pageTransform, texture!.Value.Bitmap, texture.Value.Key.PxPerPt, content, pdfDims, pdf);
                break;

            case PdfObjectDrawAction.RequestTexture:
                DrawPlaceholder(session, pageTransform, pdf);
                BeginRender(state!, pdf, content, displayPxPerPt);
                break;

            case PdfObjectDrawAction.MissingResource:
                DrawPlaceholder(session, pageTransform, pdf);
                if (state is { } st && !st.OpenInProgress && DateTime.UtcNow >= st.RetryAfterUtc)
                {
                    BeginOpen(st, pdf.ResourceId); // 再試行 (間隔管理済み)。
                }

                break;

            default:
                // PendingAlready / PageOutOfRange / ResourceNotReady → プレースホルダのみ。
                DrawPlaceholder(session, pageTransform, pdf);
                break;
        }
    }

    /// <summary>
    /// オブジェクト 1 個の描画判断 (Draw と OnContentChanged で共用)。
    /// </summary>
    private PdfObjectDrawAction Evaluate(
        PdfObject pdf,
        double displayPxPerPt,
        out (CanvasBitmap Bitmap, PdfObjectTextureKey Key)? texture,
        out (float ContentWidthPt, float ContentHeightPt, Vector2 ContentCenter, float RotationRadians) content,
        out (float PdfPageWidthPt, float PdfPageHeightPt) pdfDims,
        out ResourceState? state)
    {
        texture = null;
        content = default;
        pdfDims = default;
        state = GetResourceState(pdf.ResourceId);

        if (state.Missing || state.OpenFailed)
        {
            return PdfObjectDrawAction.MissingResource;
        }

        if (!state.PageCountKnown)
        {
            return PdfObjectDrawAction.ResourceNotReady;
        }

        content = PdfObjectMath.GetContentRect(pdf.X, pdf.Y, pdf.WidthPt, pdf.HeightPt, pdf.RotationDeg);

        bool outOfRange;
        bool dimsKnown;
        double pendingRequired = 0;
        lock (state.Gate)
        {
            outOfRange = (uint)pdf.PdfPageIndex >= (uint)state.PageCount ||
                         (state.PageRetryAfterUtc.TryGetValue(pdf.PdfPageIndex, out var retry) &&
                          DateTime.UtcNow < retry);
            dimsKnown = state.Dims.TryGetValue(pdf.PdfPageIndex, out var dims);
            if (dimsKnown)
            {
                pdfDims = ((float)dims.WidthPt, (float)dims.HeightPt);
                var required = PdfObjectMath.EffectivePxPerPt(
                    displayPxPerPt, content.ContentWidthPt, content.ContentHeightPt, dims.WidthPt, dims.HeightPt);
                // required はローカルで pending 判定に使用。
                pendingRequired = required;
            }
        }

        if (outOfRange)
        {
            return PdfObjectDrawAction.PageOutOfRange;
        }

        var hasTexture = _textures.TryGetAny(pdf.Id, out var bitmap, out var texKey);
        var usable = dimsKnown && hasTexture && PdfObjectMath.IsTextureUsable(texKey.PxPerPt, pendingRequired);
        var pendingSameScale = false;
        var pendingOtherScale = false;
        var pendingKey = pdf.Id;
        if (_pending.TryGetValue(pendingKey, out var pendingRequest))
        {
            if (pendingRequest is null || pendingRequired <= 0 ||
                Math.Abs(pendingRequest.RequestedPxPerPt / Math.Max(pendingRequired, 0.0001) - 1.0) < 0.02)
            {
                pendingSameScale = true;
            }
            else
            {
                pendingOtherScale = true;
            }
        }

        if (hasTexture)
        {
            texture = (bitmap, texKey);
        }

        return PdfObjectDrawPlanner.Decide(
            missingResource: false,
            pageOutOfRange: false,
            hasTexture: hasTexture,
            textureUsable: usable,
            pendingSameScale: pendingSameScale,
            pendingOtherScale: pendingOtherScale);
    }

    private static void DrawTexture(
        CanvasDrawingSession session, Matrix3x2 pageTransform, CanvasBitmap bitmap,
        double texPxPerPt,
        (float ContentWidthPt, float ContentHeightPt, Vector2 ContentCenter, float RotationRadians) content,
        (float PdfPageWidthPt, float PdfPageHeightPt) pdfDims,
        PdfObject pdf)
    {
        // バウンディング中心 (90/270 度では内容中心と異なる)。
        var boundingCenter = new Vector2(pdf.X + pdf.WidthPt / 2f, pdf.Y + pdf.HeightPt / 2f);
        var previous = session.Transform;
        session.Transform = PdfObjectMath.CreateObjectTransform(
            texPxPerPt,
            content.ContentWidthPt, content.ContentHeightPt,
            pdfDims.PdfPageWidthPt, pdfDims.PdfPageHeightPt,
            boundingCenter, content.RotationRadians, pageTransform);
        session.DrawImage(bitmap);
        session.Transform = previous;
    }

    private static void DrawPlaceholder(CanvasDrawingSession session, Matrix3x2 pageTransform, PdfObject pdf)
    {
        var previous = session.Transform;
        session.Transform = pageTransform;
        session.FillRectangle(pdf.X, pdf.Y, pdf.WidthPt, pdf.HeightPt, PlaceholderFill);
        session.DrawRectangle(pdf.X, pdf.Y, pdf.WidthPt, pdf.HeightPt, PlaceholderBorder, 1f);
        session.Transform = previous;
    }

    // ------------------------------------------------------------
    // リソース状態。
    // ------------------------------------------------------------
    private ResourceState GetResourceState(Guid resourceId)
    {
        if (_resourceStates.TryGetValue(resourceId, out var state))
        {
            return state;
        }

        state = new ResourceState();
        _resourceStates[resourceId] = state;
        BeginOpen(state, resourceId);
        return state;
    }

    private void BeginOpen(ResourceState state, Guid resourceId)
    {
        state.OpenInProgress = true;
        var resource = _session?.Notebook.Resources.FirstOrDefault(r => r.Id == resourceId);
        var path = resource is null || _notebookFolder is null
            ? null
            : NotebookLibrary.FindResourceFile(_notebookFolder, resource);

        if (path is null)
        {
            // 参照切れ: 再リンク UI は未実装 (ステップ6) のためプレースホルダ + 再試行。
            state.Missing = true;
            state.OpenInProgress = false;
            state.RetryAfterUtc = DateTime.UtcNow + RetryDelay;
            _invalidate();
            return;
        }

        var generation = _generation;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await _service.OpenAsync(resourceId, path).ConfigureAwait(false);
                _dispatcher.TryEnqueue(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    state.PageCount = _service.TryGetPageCount(resourceId) ?? 0;
                    state.PageCountKnown = true;
                    state.OpenInProgress = false;

                    // 実ページ数による既存オブジェクトの検証 (削除せず、警告付きプレースホルダとして保持)。
                    var invalid = 0;
                    var session = _session;
                    if (session is not null)
                    {
                        foreach (var nbPage in session.Notebook.Pages)
                        {
                            foreach (var obj in nbPage.Objects)
                            {
                                if (obj is PdfObject p && p.ResourceId == resourceId &&
                                    ((uint)p.PdfPageIndex >= (uint)state.PageCount || p.PdfPageIndex < 0))
                                {
                                    invalid++;
                                }
                            }
                        }
                    }

                    state.InvalidObjectCount = invalid;
                    if (invalid > 0 && !state.WarnedInvalidObjects)
                    {
                        state.WarnedInvalidObjects = true;
                        App.LogError(
                            $"PDF リソース {resourceId} のページ番号が実ページ数 ({state.PageCount}) の範囲外です ({invalid} 件。プレースホルダで保持します)。", null);
                    }

                    _invalidate();
                });
            }
            catch (Exception ex)
            {
                // 破損 / アクセス拒否など。クラッシュさせずプレースホルダ + 再試行。
                // ログは状態変化時に 1 回だけ (毎フレーム大量出力の防止)。
                _dispatcher.TryEnqueue(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    state.OpenFailed = true;
                    state.OpenInProgress = false;
                    state.RetryAfterUtc = DateTime.UtcNow + RetryDelay;
                    if (!state.LoggedOpenFailure)
                    {
                        state.LoggedOpenFailure = true;
                        App.LogError($"PDF リソース {resourceId} を開けませんでした (プレースホルダ表示に切り替えます)。", ex);
                    }

                    _invalidate();
                });
            }
        });
    }

    // ------------------------------------------------------------
    // レンダリング要求。
    // ------------------------------------------------------------
    private void BeginRender(
        ResourceState state, PdfObject pdf,
        (float ContentWidthPt, float ContentHeightPt, Vector2 Center, float RotationRadians) content,
        double displayPxPerPt)
    {
        var pendingKey = pdf.Id;
        if (_pending.TryGetValue(pendingKey, out var existing))
        {
            if (existing is null)
            {
                return; // 寸法取得中 (準備中)。
            }

            var required = RequiredScale(displayPxPerPt, state, pdf, content);
            if (Math.Abs(existing.RequestedPxPerPt / Math.Max(required, 0.0001) - 1.0) < 0.02)
            {
                return; // 同一条件の要求が飛行中 (重複要求しない)。
            }

            // 異なる解像度は陳腐化するため最新の解像度で再要求 (サービスが旧待者をキャンセル)。
        }

        _pending[pendingKey] = null; // 準備中マーク (並行発火の防止)。
        var generation = _generation;
        _ = RunRenderAsync(state, pdf, content, displayPxPerPt, pendingKey, generation);
    }

    private async System.Threading.Tasks.Task RunRenderAsync(
        ResourceState state, PdfObject pdf,
        (float ContentWidthPt, float ContentHeightPt, Vector2 Center, float RotationRadians) content,
        double displayPxPerPt, Guid objectId, int generation)
    {
        try
        {
            // ページ寸法 (バックグラウンド・キャッシュ済み)。以降の Draw / 再要求で再利用される。
            var dims = await GetDimsAsync(state, pdf).ConfigureAwait(false);
            if (generation != _generation)
            {
                return; // ノート切替後は反映しない。
            }

            // PDF 実ページ数による検証 (要求直前の再確認)。
            var pageCount = _service.TryGetPageCount(pdf.ResourceId);
            if (pageCount is null || (uint)pdf.PdfPageIndex >= (uint)pageCount)
            {
                _pending.Remove(objectId);
                return;
            }

            var required = PdfObjectMath.EffectivePxPerPt(
                displayPxPerPt, content.ContentWidthPt, content.ContentHeightPt, dims.WidthPt, dims.HeightPt);
            var request = _service.Render(pdf.ResourceId, pdf.PdfPageIndex, required);
            _pending[objectId] = request;
            _latestRequestId[objectId] = request.RequestId;

            var requestId = request.RequestId;
            _ = request.Task.ContinueWith(
                t => OnRenderTaskCompleted(generation, pdf, requestId, t),
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception)
        {
            // 寸法取得 / 要求発行の失敗。クラッシュさせず再試行時刻を設定。
            _pending.Remove(objectId);
            if (generation == _generation)
            {
                lock (state.Gate)
                {
                    state.PageRetryAfterUtc[pdf.PdfPageIndex] = DateTime.UtcNow + RetryDelay;
                }

                _invalidate();
            }
        }
    }

    private async System.Threading.Tasks.Task<(double WidthPt, double HeightPt)> GetDimsAsync(
        ResourceState state, PdfObject pdf)
    {
        lock (state.Gate)
        {
            if (state.Dims.TryGetValue(pdf.PdfPageIndex, out var known) && known.WidthPt > 0)
            {
                return known;
            }
        }

        var dims = await _service.GetPageSizePtAsync(pdf.ResourceId, pdf.PdfPageIndex).ConfigureAwait(false);
        lock (state.Gate)
        {
            state.Dims[pdf.PdfPageIndex] = dims;
        }

        return dims;
    }

    private static double RequiredScale(
        double displayPxPerPt, ResourceState state, PdfObject pdf,
        (float ContentWidthPt, float ContentHeightPt, Vector2 Center, float RotationRadians) content)
    {
        lock (state.Gate)
        {
            if (state.Dims.TryGetValue(pdf.PdfPageIndex, out var dims))
            {
                return PdfObjectMath.EffectivePxPerPt(
                    displayPxPerPt, content.ContentWidthPt, content.ContentHeightPt, dims.WidthPt, dims.HeightPt);
            }
        }

        return displayPxPerPt;
    }

    private void OnRenderTaskCompleted(
        int generation, PdfObject pdf, long requestId,
        System.Threading.Tasks.Task<PdfObjectRenderResult> task)
    {
        // RequestId 照合: 古い (キャンセル対応前に完了した) 結果は破棄する。
        // ノート切替後 (generation 不一致) の結果も反映しない。
        var latestMatches = _latestRequestId.TryGetValue(pdf.Id, out var latest) && latest == requestId;
        if (generation != _generation)
        {
            return; // ノート切替 (破棄カウント対象外)。
        }

        if (!latestMatches)
        {
            StaleDiscardedCount++;
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (generation != _generation)
            {
                return;
            }

            if (!_latestRequestId.TryGetValue(pdf.Id, out var latestNow) || latestNow != requestId)
            {
                StaleDiscardedCount++; // ページ切替 / より新しい要求が到着済み。
                return;
            }

            _pending.Remove(pdf.Id);

            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
            {
                var frame = task.Result.Frame;
                if (!PdfObjectMath.ExceedsTextureBudget(frame.PixelWidth, frame.PixelHeight))
                {
                    var bitmap = CanvasBitmap.CreateFromBytes(
                        CanvasDevice.GetSharedDevice(),
                        frame.Pixels,
                        frame.PixelWidth,
                        frame.PixelHeight,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        96f,
                        CanvasAlphaMode.Ignore);
                    foreach (var (_, evicted) in _textures.Add(
                        new PdfObjectTextureKey(pdf.Id, pdf.ResourceId, pdf.PdfPageIndex, task.Result.PxPerPt),
                        bitmap, frame.PixelWidth, frame.PixelHeight))
                    {
                        evicted.Dispose(); // 退避テクスチャは即解放 (UI スレッド)。
                    }
                }
            }
            else if (task.Status == System.Threading.Tasks.TaskStatus.Faulted)
            {
                var state = _resourceStates.TryGetValue(pdf.ResourceId, out var found) ? found : null;
                if (state is not null)
                {
                    lock (state.Gate)
                    {
                        state.PageRetryAfterUtc[pdf.PdfPageIndex] = DateTime.UtcNow + RetryDelay;
                    }
                }
            }

            // Canceled: より新しい要求が飛行中のため何もしない。
            _invalidate();
        });
    }
}
