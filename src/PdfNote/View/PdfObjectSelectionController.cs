using System;
using System.Linq;
using System.Numerics;
using PdfNote.Core;
using PdfNote.Session;

namespace PdfNote.View;

/// <summary>
/// 選択ツールの操作コントローラ (ページ座標系で動作。Win2D 非依存のためヒット判定はテスト可能)。
/// - 押下: ハンドル (リサイズ) → 本体 (移動+選択) → それ以外 (選択解除) の優先順。
/// - ヒットは描画順の逆 (背面から前面) で走査し、最初に当たった=最前面を選択。
/// - ドラッグ中はプレビュー (履歴なし) を適用し、終了時に 1 操作として履歴へ積む。
/// - キャンセル時は開始値へ戻し、履歴を積まない。
/// </summary>
public sealed class PdfObjectSelectionController
{
    private readonly NotebookSession _session;
    private readonly PdfObjectLayer _layer;
    private readonly Action _invalidate;

    /// <summary>現在の表示スケール (px/pt)。MainWindow がズームに合わせて更新する。ハンドルの画面固定サイズに使用。</summary>
    public double DisplayPxPerPt { get; set; } = RenderScale.DefaultPxPerPt;

    public Guid? SelectedObjectId => _layer.SelectedObjectId;

    private enum DragKind
    {
        None,
        Move,
        Resize,
    }

    private DragKind _kind = DragKind.None;
    private PdfObject? _object;
    private ObjectTransform _startTransform;
    private Vector2 _startPagePoint;
    private int _resizeCorner;

    public PdfObjectSelectionController(NotebookSession session, PdfObjectLayer layer, Action invalidate)
    {
        _session = session;
        _layer = layer;
        _invalidate = invalidate;
    }

    public void ClearSelection()
    {
        if (_layer.SelectedObjectId is not null)
        {
            _layer.SelectedObjectId = null;
            _invalidate();
        }
    }

    /// <summary>選択ハンドルの半サイズ (ページpt)。8px ハンドルを画面固定で表示する。</summary>
    private float HandleHalfPt =>
        (float)(4.0 / Math.Max(DisplayPxPerPt, 0.1));

    /// <summary>押下処理。オブジェクト操作を開始した (または選択解除した) 場合 true を返す (パンしない)。</summary>
    public bool PointerPressed(Vector2 pagePoint)
    {
        var page = _session.CurrentPage;
        if ((uint)page >= (uint)_session.PageCount)
        {
            ClearSelection();
            return false;
        }

        var objects = _session.GetObjectsInDrawOrder(page);

        // 選択中オブジェクトのハンドル / 本体を優先。
        if (_layer.SelectedObjectId is { } selectedId)
        {
            var selected = objects.FirstOrDefault(o => o.Id == selectedId);
            if (selected is PdfObject selectedPdf)
            {
                var corner = PdfObjectMath.HitTestHandle(
                    selectedPdf.X, selectedPdf.Y, selectedPdf.WidthPt, selectedPdf.HeightPt,
                    pagePoint, HandleHalfPt);
                if (corner >= 0)
                {
                    BeginDrag(DragKind.Resize, selectedPdf, pagePoint, corner);
                    return true;
                }

                if (PdfObjectMath.HitTestObject(
                        selectedPdf.X, selectedPdf.Y, selectedPdf.WidthPt, selectedPdf.HeightPt,
                        selectedPdf.RotationDeg, pagePoint))
                {
                    BeginDrag(DragKind.Move, selectedPdf, pagePoint, 0);
                    return true;
                }
            }

            ClearSelection(); // オブジェクト外クリック → 選択解除。
        }

        // 最前面 (描画順の末尾) から走査。
        for (var i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i] is PdfObject pdf &&
                PdfObjectMath.HitTestObject(
                    pdf.X, pdf.Y, pdf.WidthPt, pdf.HeightPt, pdf.RotationDeg, pagePoint))
            {
                _layer.SelectedObjectId = pdf.Id;
                BeginDrag(DragKind.Move, pdf, pagePoint, 0);
                _invalidate();
                return true;
            }
        }

        return false;
    }

    /// <summary>ドラッグ中の移動 (プレビュー適用。ページ座標差分なのでズーム/パンに依存しない)。</summary>
    public void PointerMoved(Vector2 pagePoint)
    {
        if (_kind == DragKind.None || _object is null)
        {
            return;
        }

        switch (_kind)
        {
            case DragKind.Move:
                var moved = PdfObjectMath.TranslateTransform(_startTransform, pagePoint - _startPagePoint);
                _session.TryPreviewObjectTransform(_session.CurrentPage, _object.Id, moved);
                break;

            case DragKind.Resize:
                var resized = PdfObjectMath.ResizeFromCorner(
                    _startTransform, _resizeCorner, pagePoint, PageObjectLimits.MinObjectSizePt);
                _session.TryPreviewObjectTransform(_session.CurrentPage, _object.Id, resized);
                break;
        }
    }

    /// <summary>
    /// 解放: 開始時からの変形を 1 操作として履歴に積む (移動量ゼロなら積まない)。
    /// 戻り値は押下でドラッグを開始していたか。
    /// </summary>
    public bool PointerReleased()
    {
        if (_kind == DragKind.None || _object is null)
        {
            return false;
        }

        var objectId = _object.Id;
        var before = _startTransform;
        EndDrag();
        _session.TryCommitObjectTransform(_session.CurrentPage, objectId, before);
        _invalidate();
        return true;
    }

    /// <summary>ドラッグのキャンセル (ポインタロスト等): 開始値へ戻し、履歴に積まない。</summary>
    public void CancelDrag()
    {
        if (_kind == DragKind.None || _object is null)
        {
            return;
        }

        var objectId = _object.Id;
        var before = _startTransform;
        EndDrag();
        _session.TryRestoreObjectTransform(_session.CurrentPage, objectId, before);
        _invalidate();
    }

    /// <summary>選択中オブジェクトを時計回りに 90° 回転する (1 履歴。バウンディング矩形は保持)。</summary>
    public void RotateSelectedClockwise()
    {
        var obj = GetSelectedObject();
        if (obj is null)
        {
            return;
        }

        var current = ObjectTransform.Capture(obj);
        var rotated = current with { RotationDeg = (current.RotationDeg + 90) % 360 };
        if (rotated.RotationDeg == current.RotationDeg)
        {
            return;
        }

        _session.TrySetObjectTransform(_session.CurrentPage, obj.Id, rotated);
        _invalidate();
    }

    /// <summary>選択中オブジェクトを最前面へ。</summary>
    public void BringToFront()
    {
        var obj = GetSelectedObject();
        if (obj is null)
        {
            return;
        }

        _session.TryRaiseObjectZ(_session.CurrentPage, obj.Id);
        _invalidate();
    }

    /// <summary>選択中オブジェクトを最背面へ。</summary>
    public void SendToBack()
    {
        var obj = GetSelectedObject();
        if (obj is null)
        {
            return;
        }

        _session.TryLowerObjectZ(_session.CurrentPage, obj.Id);
        _invalidate();
    }

    private PdfObject? GetSelectedObject()
    {
        if (_layer.SelectedObjectId is not { } id)
        {
            return null;
        }

        var page = _session.CurrentPage;
        if ((uint)page >= (uint)_session.PageCount)
        {
            return null;
        }

        return _session.GetObjectsInDrawOrder(page).FirstOrDefault(o => o.Id == id) as PdfObject;
    }

    private void BeginDrag(DragKind kind, PdfObject pdfObject, Vector2 pagePoint, int corner)
    {
        _kind = kind;
        _object = pdfObject;
        _startTransform = ObjectTransform.Capture(pdfObject);
        _startPagePoint = pagePoint;
        _resizeCorner = corner;
    }

    private void EndDrag()
    {
        _kind = DragKind.None;
        _object = null;
        _startTransform = default;
    }
}
