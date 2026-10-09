using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Microsoft.UI.Input;
using PdfNote.Core;
using PdfNote.Input;

namespace PdfNote.View;

/// <summary>
/// PDF ページ表示ビュー。
/// 内部に Win2D の CanvasControl (sealed) を保持し、
/// 現在の GPU テクスチャ (CanvasBitmap) を 1 枚だけ保持する。
/// パン/ズームは行列変換のみで処理する (PDF の再レンダリングは発生しない)。
/// </summary>
public sealed class PdfCanvas : UserControl
{
    private static readonly Color BorderColor = Color.FromArgb(255, 96, 96, 96);
    private static readonly Color GridLineColor = Color.FromArgb(255, 208, 208, 208);
    private static readonly Color PlaceholderColor = Color.FromArgb(255, 170, 170, 170);

    /// <summary>ページがビュー外に出すぎないように残す余白 (DIP)。</summary>
    private const float EdgeKeep = 80f;

    /// <summary>ズーム操作が落ち着いたとみなす時間。</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(350);

    private readonly CanvasControl _canvas;
    private RenderedFrame? _currentFrame;
    private CanvasBitmap? _bitmap;
    private double _bitmapPxPerPt = RenderScale.DefaultPxPerPt;
    private double _displayPxPerPt = RenderScale.DefaultPxPerPt;
    private Vector2 _panOffset; // ページ左上の表示位置 (コントロール座標 DIP)
    private double _pageWidthPt;
    private double _pageHeightPt;
    private PageBackgroundKind _backgroundKind = PageBackgroundKind.Plain;
    private float _gridSpacingPt = NotebookDefaults.GridSpacingPt;
    private string _placeholder = "Ctrl+O で PDF を開いてください";
    private bool _pendingFit = true;

    private readonly DispatcherQueueTimer _settleTimer;
    private readonly Dictionary<uint, Vector2> _activePointers = new();
    private float _pinchStartDistance;
    private double _pinchStartDisplayScale;
    private Vector2 _pinchStartMid;

    /// <summary>表示解像度が変わった (ズーム操作のたび、UI スレッド)。</summary>
    public event Action<double>? DisplayScaleChanged;

    /// <summary>ズーム操作が落ち着いた (UI スレッド)。</summary>
    public event Action<double>? ZoomSettled;

    /// <summary>ペン入力イベント (Ink Layer への転送。UI スレッド)。</summary>
    public event Action<InkPointerEvent>? InkPointerReceived;

    /// <summary>Ink 描画タイミング。session.Transform はページ pt 空間で呼ばれる (UI スレッド)。</summary>
    public event Action<Microsoft.Graphics.Canvas.CanvasDrawingSession>? InkDrawing;

    public double DisplayPxPerPt => _displayPxPerPt;

    public string PlaceholderText
    {
        get => _placeholder;
        set
        {
            _placeholder = value;
            _canvas.Invalidate();
        }
    }

    public PdfCanvas()
    {
        _canvas = new CanvasControl
        {
            ClearColor = Color.FromArgb(255, 32, 32, 32),
        };
        Content = _canvas;

        _canvas.Draw += OnDraw;
        _canvas.CreateResources += OnCreateResources;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerCanceled;
        _canvas.PointerCaptureLost += OnPointerCaptureLost;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;

        _settleTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settleTimer.Interval = SettleDelay;
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) => ZoomSettled?.Invoke(_displayPxPerPt);

        SizeChanged += (_, _) =>
        {
            if (_pendingFit)
            {
                FitToViewport();
            }
            else
            {
                ClampPan();
                _canvas.Invalidate();
            }
        };
    }

    /// <summary>ページ寸法 (pt) を設定する。</summary>
    public void SetPageGeometry(double widthPt, double heightPt)
    {
        _pageWidthPt = widthPt;
        _pageHeightPt = heightPt;
        _pendingFit = true;
    }

    /// <summary>ページ背景を設定する (ノートブックモード。ページ切替ごとに呼ぶ)。</summary>
    public void SetPageBackground(PageBackgroundKind kind, float gridSpacingPt)
    {
        _backgroundKind = kind;
        _gridSpacingPt = Math.Max(gridSpacingPt, 1f);
        _canvas.Invalidate();
    }

    /// <summary>表示中の PDF フレームを破棄する (PDF → ノートブックへの切替時)。</summary>
    public void ClearFrame()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _currentFrame = null;
        _canvas.Invalidate();
    }

    /// <summary>ビューにフィットするズーム + 位置にリセットする。</summary>
    public void FitToViewport(double margin = 24)
    {
        if (_pageWidthPt <= 0 || _pageHeightPt <= 0 || ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        var viewportWidth = ActualWidth - margin * 2;
        var viewportHeight = ActualHeight - margin * 2;
        var scale = Math.Min(viewportWidth / _pageWidthPt, viewportHeight / _pageHeightPt);
        scale = Math.Clamp(scale, RenderScale.MinPxPerPt, RenderScale.MaxPxPerPt);

        _displayPxPerPt = scale;
        _panOffset = new Vector2(
            (float)((ActualWidth - _pageWidthPt * scale) / 2),
            (float)((ActualHeight - _pageHeightPt * scale) / 2));
        _pendingFit = false;

        DisplayScaleChanged?.Invoke(scale);
        RestartSettleTimer();
        _canvas.Invalidate();
    }

    /// <summary>現在のズームを維持したままページを上中央に配置する (ページ移動後)。</summary>
    public void CenterPage()
    {
        if (_pageWidthPt <= 0)
        {
            return;
        }

        _panOffset = new Vector2(
            (float)((ActualWidth - _pageWidthPt * _displayPxPerPt) / 2),
            24f);
        ClampPan();
        _canvas.Invalidate();
    }

    /// <summary>表示倍率を設定する (アンカー点中心)。</summary>
    public void SetDisplayScale(double pxPerPt, Vector2? anchor = null)
    {
        var clamped = Math.Clamp(pxPerPt, RenderScale.MinPxPerPt, RenderScale.MaxPxPerPt);
        if (Math.Abs(clamped - _displayPxPerPt) < 0.0001)
        {
            return;
        }

        ZoomAtInternal(clamped, anchor ?? ViewportCenter());
    }

    /// <summary>相対パン (表示座標 DIP)。</summary>
    public void PanBy(Vector2 delta)
    {
        _panOffset += delta;
        ClampPan();
        _canvas.Invalidate();
    }

    /// <summary>再描画要求 (Ink Layer から)。</summary>
    public void Invalidate() => _canvas.Invalidate();

    /// <summary>レンダリング済みフレームを GPU テクスチャとして設定する (UI スレッド専用)。</summary>
    public void SetFrame(RenderedFrame frame)
    {
        _currentFrame = frame;
        // 共有デバイス (CanvasControl の準備状態に依存しない。UseSharedDevice 既定=true で互換)。
        // Pixels は PdfDocument 側で白合成済みの不透明 BGRA なので Ignore で扱う。
        var bitmap = CanvasBitmap.CreateFromBytes(
            CanvasDevice.GetSharedDevice(),
            frame.Pixels,
            frame.PixelWidth,
            frame.PixelHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96f,
            CanvasAlphaMode.Ignore);

        _bitmap?.Dispose();
        _bitmap = bitmap;
        _bitmapPxPerPt = frame.PxPerPt;
        _canvas.Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);

        // ペンは Ink Layer へ転送 (パン/ズームとは独立)。
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            _canvas.CapturePointer(e.Pointer);
            DispatchInkEvent(e, point, InkPhase.Pressed);
            e.Handled = true;
            return;
        }

        _activePointers[e.Pointer.PointerId] = point.Position.ToVector2();
        _canvas.CapturePointer(e.Pointer);
        if (_activePointers.Count == 2)
        {
            BeginPinch();
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);

        // ペンは Ink Layer へ転送 (hover 含む。InkSession 側で接触状態を判定する)。
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            DispatchInkEvent(e, point, InkPhase.Moved);
            e.Handled = true;
            return;
        }

        if (!_activePointers.TryGetValue(e.Pointer.PointerId, out var previous))
        {
            return;
        }

        var current = point.Position.ToVector2();
        _activePointers[e.Pointer.PointerId] = current;

        if (_activePointers.Count == 1)
        {
            PanBy(current - previous);
        }
        else if (_activePointers.Count >= 2)
        {
            UpdatePinch();
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            DispatchInkEvent(e, e.GetCurrentPoint(_canvas), InkPhase.Released);
            _canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        RemovePointer(e);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            DispatchInkEvent(e, e.GetCurrentPoint(_canvas), InkPhase.Canceled);
            _canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        RemovePointer(e);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            DispatchInkEvent(e, e.GetCurrentPoint(_canvas), InkPhase.Canceled);
            return;
        }

        RemovePointer(e);
    }

    private void DispatchInkEvent(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint point, InkPhase phase)
    {
        if (InkPointerReceived is null)
        {
            return;
        }

        var position = point.Position.ToVector2();
        var pagePt = _pageWidthPt > 0
            ? (position - _panOffset) / (float)_displayPxPerPt
            : Vector2.Zero;

        var inkEvent = new InkPointerEvent(
            phase,
            PointerKind.Pen,
            e.Pointer.PointerId,
            pagePt.X,
            pagePt.Y,
            point.Properties.Pressure,
            point.Properties.IsBarrelButtonPressed,
            point.Properties.IsEraser);

        InkPointerReceived.Invoke(inkEvent);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        var factor = Math.Pow(1.15, point.Properties.MouseWheelDelta / 120.0);
        SetDisplayScale(_displayPxPerPt * factor, point.Position.ToVector2());
        e.Handled = true;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        var hasPage = _pageWidthPt > 0 && _pageHeightPt > 0;

        if (_bitmap is null && !hasPage)
        {
            DrawPlaceholder(sender, session);
            return;
        }

        // ページ空間 (表示スケール + パン) の変換。背景と Ink で共有する。
        var pageTransform = Matrix3x2.CreateScale((float)_displayPxPerPt) * Matrix3x2.CreateTranslation(_panOffset);

        if (_bitmap is not null)
        {
            var scale = (float)(_displayPxPerPt / _bitmapPxPerPt);
            session.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(_panOffset);

            // PDFium の出力は背景が透明なため、白背景の上に Straight アルファで合成する。
            var size = _bitmap.Size;
            session.FillRectangle(0f, 0f, (float)size.Width, (float)size.Height, Colors.White);
            session.DrawImage(_bitmap);

            // ページ外枠 (ズームで線幅が変わらないように逆スケール)。
            session.DrawRectangle(0f, 0f, (float)size.Width, (float)size.Height, BorderColor, 1f / Math.Max(scale, 0.0001f));
        }
        else if (hasPage)
        {
            // PDF テクスチャのないページ (ノートブックモードの白紙/方眼)。
            session.Transform = pageTransform;
            var width = (float)_pageWidthPt;
            var height = (float)_pageHeightPt;
            session.FillRectangle(0f, 0f, width, height, Colors.White);
            if (_backgroundKind == PageBackgroundKind.Grid)
            {
                DrawGrid(session, width, height);
            }

            session.DrawRectangle(0f, 0f, width, height, BorderColor, 1f / Math.Max((float)_displayPxPerPt, 0.0001f));
        }

        // Ink Layer (背景/PDF テクスチャより前面の合成パス)。
        if (InkDrawing is not null && hasPage)
        {
            var previousTransform = session.Transform;
            session.Transform = pageTransform;
            InkDrawing.Invoke(session);
            session.Transform = previousTransform;
        }
    }

    /// <summary>方眼背景をページ座標 (pt) に沿って描画する。線は画面上 1px (逆スケール)。</summary>
    private void DrawGrid(CanvasDrawingSession session, float widthPt, float heightPt)
    {
        var spacing = Math.Max(_gridSpacingPt, 1f);
        var strokeWidth = 1f / Math.Max((float)_displayPxPerPt, 0.0001f);

        for (var x = spacing; x < widthPt - 0.01f; x += spacing)
        {
            session.DrawLine(new Vector2(x, 0f), new Vector2(x, heightPt), GridLineColor, strokeWidth);
        }

        for (var y = spacing; y < heightPt - 0.01f; y += spacing)
        {
            session.DrawLine(new Vector2(0f, y), new Vector2(widthPt, y), GridLineColor, strokeWidth);
        }
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // デバイスが (再) 生成された場合は保持しているフレームからビットマップを作り直す。
        if (args.Reason != CanvasCreateResourcesReason.FirstTime && _currentFrame is { } frame)
        {
            _bitmap?.Dispose();
            _bitmap = CanvasBitmap.CreateFromBytes(
                CanvasDevice.GetSharedDevice(),
                frame.Pixels,
                frame.PixelWidth,
                frame.PixelHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                96f,
                CanvasAlphaMode.Ignore);
            _bitmapPxPerPt = frame.PxPerPt;
        }
    }

    private void DrawPlaceholder(CanvasControl sender, CanvasDrawingSession session)
    {
        var size = sender.ActualSize;
        if (size.X < 1 || size.Y < 1)
        {
            return;
        }

        using var format = new CanvasTextFormat
        {
            FontSize = 16,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        using var layout = new CanvasTextLayout(sender, _placeholder, format, (float)size.X, (float)size.Y);
        session.DrawTextLayout(layout, Vector2.Zero, PlaceholderColor);
    }


    private void BeginPinch()
    {
        var (a, b) = GetTwoPointerPositions();
        _pinchStartDistance = Math.Max(Vector2.Distance(a, b), 1f);
        _pinchStartMid = (a + b) / 2f;
        _pinchStartDisplayScale = _displayPxPerPt;
    }

    private void UpdatePinch()
    {
        var (a, b) = GetTwoPointerPositions();
        var distance = Math.Max(Vector2.Distance(a, b), 1f);
        var mid = (a + b) / 2f;

        SetDisplayScale(_pinchStartDisplayScale * distance / _pinchStartDistance, mid);
        PanBy(mid - _pinchStartMid);
        _pinchStartMid = mid;
    }

    private (Vector2 A, Vector2 B) GetTwoPointerPositions()
    {
        using var enumerator = _activePointers.GetEnumerator();
        enumerator.MoveNext();
        var first = enumerator.Current.Value;
        enumerator.MoveNext();
        var second = enumerator.Current.Value;
        return (first, second);
    }

    private void RemovePointer(PointerRoutedEventArgs e)
    {
        _activePointers.Remove(e.Pointer.PointerId);
        _canvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void ZoomAtInternal(double newScale, Vector2 anchor)
    {
        // アンカー点の下にあるページ座標 (pt) を固定したままスケールを変更する。
        var contentPt = (anchor - _panOffset) / (float)_displayPxPerPt;
        _displayPxPerPt = newScale;
        _panOffset = anchor - contentPt * (float)newScale;
        ClampPan();
        DisplayScaleChanged?.Invoke(newScale);
        RestartSettleTimer();
        _canvas.Invalidate();
    }

    private void ClampPan()
    {
        if (_pageWidthPt <= 0 || _pageHeightPt <= 0)
        {
            return;
        }

        var viewportWidth = (float)ActualWidth;
        var viewportHeight = (float)ActualHeight;
        if (viewportWidth < 1 || viewportHeight < 1)
        {
            return;
        }

        var pageWidth = (float)(_pageWidthPt * _displayPxPerPt);
        var pageHeight = (float)(_pageHeightPt * _displayPxPerPt);

        _panOffset.X = pageWidth <= viewportWidth
            ? (viewportWidth - pageWidth) / 2f
            : Math.Clamp(_panOffset.X, EdgeKeep - pageWidth, viewportWidth - EdgeKeep);

        _panOffset.Y = pageHeight <= viewportHeight
            ? (viewportHeight - pageHeight) / 2f
            : Math.Clamp(_panOffset.Y, EdgeKeep - pageHeight, viewportHeight - EdgeKeep);
    }

    private Vector2 ViewportCenter() => new((float)(ActualWidth / 2), (float)(ActualHeight / 2));

    private void RestartSettleTimer()
    {
        _settleTimer.Stop();
        _settleTimer.Start();
    }
}

internal static class PointerExtensions
{
    public static Vector2 ToVector2(this Point point)
        => new((float)point.X, (float)point.Y);
}

