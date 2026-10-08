using System;
using System.Numerics;
using PdfNote.Core;
using PdfNote.Diagnostics;
using PdfNote.Input;
using PdfNote.View;

namespace PdfNote.Ink;

/// <summary>
/// Ink 入力〜モデル〜レンダリングの中継。
/// PDF Renderer とは独立しており、ペン入力中は PDF を一切再レンダリングしない。
/// </summary>
public sealed class InkSession
{
    /// <summary>消しゴム半径 (pt)。</summary>
    private const float EraserRadiusPt = 8f;

    /// <summary>ポイント追加の最小移動距離 (pt)。微小ノイズ点の混入を防ぐ。</summary>
    private const float MinPointDistancePt = 0.5f;

    private readonly PdfCanvas _canvas;
    private readonly PerfMonitor _perf;
    private readonly InkDocument _document = new();
    private readonly InkUndoStack _undo = new();
    private readonly InkRenderer _renderer = new();
    private readonly InkToolController _tools = new();

    private Stroke? _activeStroke;
    private uint _activePointerId;
    private bool _pointerDown;
    private bool _barrelDown;
    private bool _eraserEndDown;
    private bool _panning;
    private Vector2 _lastPanPosition;
    private int _currentPage;
    private bool _hasPageGeometry;

    public InkSession(PdfCanvas canvas, PerfMonitor perf)
    {
        _canvas = canvas;
        _perf = perf;

        canvas.InkPointerReceived += OnPointerEvent;
        canvas.InkDrawing += OnDrawInk;
        canvas.DisplayScaleChanged += scale => _renderer.SetDisplayScale(scale);

        _tools.ToolChanged += tool => ToolChanged?.Invoke(tool);
        _tools.ActionRequested += OnActionRequested;
    }

    /// <summary>実効ツールが変わった (UI 更新用)。</summary>
    public event Action<InkTool>? ToolChanged;

    /// <summary>Undo/Redo 可否が変わった (UI 更新用)。</summary>
    public event Action<bool, bool>? UndoRedoChanged;

    public InkTool CurrentTool => _tools.CurrentTool;

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    /// <summary>ツールを設定する (ツールバーボタン)。</summary>
    public void SetTool(InkTool tool) => _tools.SetBaseTool(tool);

    /// <summary>Undo 操作。</summary>
    public void Undo()
    {
        if (_activeStroke is not null)
        {
            return;
        }

        var operation = _undo.Undo(_document);
        if (operation is not null)
        {
            _renderer.MarkPageDirty(StrokePageIndex(operation));
            _canvas.Invalidate();
        }

        UndoRedoChanged?.Invoke(_undo.CanUndo, _undo.CanRedo);
    }

    /// <summary>Redo 操作。</summary>
    public void Redo()
    {
        if (_activeStroke is not null)
        {
            return;
        }

        var operation = _undo.Redo(_document);
        if (operation is not null)
        {
            _renderer.MarkPageDirty(StrokePageIndex(operation));
            _canvas.Invalidate();
        }

        UndoRedoChanged?.Invoke(_undo.CanUndo, _undo.CanRedo);
    }

    /// <summary>現在ページとページ寸法を設定する (ページ移動時)。</summary>
    public void SetPage(int pageIndex, double widthPt, double heightPt)
    {
        EndActiveStroke(commit: false);
        _renderer.RemovePage(_currentPage);
        _currentPage = pageIndex;
        _renderer.SetPageGeometry(pageIndex, widthPt, heightPt);
        _renderer.SetDisplayScale(_canvas.DisplayPxPerPt);
        _hasPageGeometry = widthPt > 0;
    }

    /// <summary>ドキュメントを閉じた/開いた時に全 Ink を破棄する。</summary>
    public void ResetDocument()
    {
        EndActiveStroke(commit: false);
        _document.Clear();
        _undo.Clear();
        _renderer.Clear();
        _hasPageGeometry = false;
        UndoRedoChanged?.Invoke(false, false);
    }

    private void OnActionRequested(InputAction action)
    {
        switch (action)
        {
            case InputAction.Undo: Undo(); break;
            case InputAction.Redo: Redo(); break;
            case InputAction.PreviousPage: PageActionRequested?.Invoke(false); break;
            case InputAction.NextPage: PageActionRequested?.Invoke(true); break;
        }
    }

    /// <summary>ペンボタンのページ移動アクション (MainWindow がセッションに接続する)。</summary>
    public event Action<bool>? PageActionRequested;

    private void OnPointerEvent(InkPointerEvent e)
    {
        if (!_hasPageGeometry)
        {
            return;
        }

        _perf.RecordPointerEvent();

        switch (e.Phase)
        {
            case InkPhase.Pressed:
                OnPointerPressed(e);
                break;
            case InkPhase.Moved:
                OnPointerMoved(e);
                break;
            case InkPhase.Released:
            case InkPhase.Canceled:
                EndActiveStroke(commit: e.Phase == InkPhase.Released && _activeStroke is not null && _activePointerId == e.PointerId);
                if (_barrelDown && !e.BarrelButton)
                {
                    _barrelDown = false;
                    _tools.ButtonUp(InkButton.Barrel);
                }
                if (_eraserEndDown)
                {
                    _eraserEndDown = false;
                    _tools.SetEraserEndActive(false);
                }
                _pointerDown = false;
                _panning = false;
                break;
        }
    }

    private void OnPointerPressed(InkPointerEvent e)
    {
        _pointerDown = true;
        _activePointerId = e.PointerId;

        // 裏消しゴム端 / バレルボタンの初期状態を反映 (アクション適用前にストロークを閉じる)。
        if (e.IsEraser)
        {
            _eraserEndDown = true;
            _tools.SetEraserEndActive(true);
        }
        if (e.BarrelButton)
        {
            _barrelDown = true;
            _tools.ButtonDown(InkButton.Barrel);
        }

        BeginStrokeOrErase(e);
    }

    private void OnPointerMoved(InkPointerEvent e)
    {
        // ボタン状態の変化を追従 (押している間だけ消しゴム 等)。
        if (e.BarrelButton != _barrelDown && _pointerDown && e.PointerId == _activePointerId)
        {
            _barrelDown = e.BarrelButton;
            if (_barrelDown)
            {
                EndActiveStroke(commit: true);
                _tools.ButtonDown(InkButton.Barrel);
            }
            else
            {
                _tools.ButtonUp(InkButton.Barrel);
            }
        }

        if (_pointerDown && e.PointerId == _activePointerId)
        {
            var position = new Vector2(e.XPt, e.YPt);

            if (_panning)
            {
                _canvas.PanBy(position - _lastPanPosition);
                _lastPanPosition = position;
                return;
            }

            var tool = _tools.CurrentTool;
            if (tool == InkTool.Eraser)
            {
                EndActiveStroke(commit: true);
                EraseAt(position);
                return;
            }

            if (_activeStroke is not null && _activeStroke.Tool == tool)
            {
                AppendPoint(e);
            }
        }
    }


    private void BeginStrokeOrErase(InkPointerEvent e)
    {
        var position = new Vector2(e.XPt, e.YPt);

        // Pan アクション中のバレルボタンはパン。
        if (_tools.BarrelAction == InputAction.Pan && _barrelDown)
        {
            _panning = true;
            _lastPanPosition = position;
            return;
        }

        var tool = _tools.CurrentTool;
        if (tool == InkTool.Eraser)
        {
            EraseAt(position);
            return;
        }

        // 描画開始。
        _activeStroke = tool == InkTool.Highlighter
            ? new Stroke(_currentPage, tool, Stroke.BaseWidthPt * 2.5f, 255, 235, 59, 90, DateTime.Now.Ticks)
            : new Stroke(_currentPage, tool, Stroke.BaseWidthPt, 20, 20, 20, 255, DateTime.Now.Ticks);

        _activeStroke.Points.Add(new InkPoint(position, SanitizePressure(e)));
        _perf.RecordStrokePoint();
        _canvas.Invalidate();
    }

    private void AppendPoint(InkPointerEvent e)
    {
        var stroke = _activeStroke!;
        var points = stroke.Points;
        var position = new Vector2(e.XPt, e.YPt);
        var last = points[points.Count - 1].Position;

        if (Vector2.DistanceSquared(last, position) < MinPointDistancePt * MinPointDistancePt)
        {
            return;
        }

        points.Add(new InkPoint(position, SanitizePressure(e)));
        _perf.RecordStrokePoint();
        _canvas.Invalidate();
    }

    private static float SanitizePressure(InkPointerEvent e)
    {
        // 筆圧非対応デバイス (マウス等) は中間値とする。
        var pressure = e.Pressure;
        if (pressure <= 0f || pressure > 1f)
        {
            pressure = 0.75f;
        }
        return pressure;
    }

    /// <summary>消しゴム: Stroke 単位で消去する (v0.2)。ヒットしたストロークを Undo 操作として記録。</summary>
    private void EraseAt(Vector2 position)
    {
        var page = _document.TryGetPage(_currentPage);
        if (page is null)
        {
            return;
        }

        for (var i = page.Strokes.Count - 1; i >= 0; i--)
        {
            var stroke = page.Strokes[i];
            if (InkHitTest.Hit(stroke, position, EraserRadiusPt))
            {
                _undo.Push(new RemoveStrokeOperation(stroke, i));
                page.Strokes.RemoveAt(i);
                _renderer.MarkPageDirty(_currentPage);
                _canvas.Invalidate();
                UndoRedoChanged?.Invoke(_undo.CanUndo, _undo.CanRedo);
                return;
            }
        }
    }

    private void EndActiveStroke(bool commit)
    {
        var stroke = _activeStroke;
        _activeStroke = null;
        _panning = false;

        if (stroke is null)
        {
            return;
        }

        if (commit && stroke.Points.Count > 0)
        {
            _document.GetOrCreatePage(stroke.PageIndex).Strokes.Add(stroke);
            _undo.Push(new AddStrokeOperation(stroke));
            _renderer.BakeStroke(stroke);
            UndoRedoChanged?.Invoke(_undo.CanUndo, _undo.CanRedo);
        }

        _canvas.Invalidate();
    }

    private void OnDrawInk(Microsoft.Graphics.Canvas.CanvasDrawingSession session)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _renderer.Draw(session, _document.TryGetPage(_currentPage), _activeStroke, _currentPage);
        stopwatch.Stop();
        _perf.RecordInkDraw(stopwatch.Elapsed.TotalMilliseconds);
    }

    private static int StrokePageIndex(IInkOperation operation) =>
        operation switch
        {
            AddStrokeOperation add => add.Stroke.PageIndex,
            RemoveStrokeOperation remove => remove.Stroke.PageIndex,
            _ => -1,
        };
}

