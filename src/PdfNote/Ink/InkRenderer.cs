using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.UI;
using PdfNote.Core;

namespace PdfNote.Ink;

/// <summary>
/// Ink Renderer。PDF Renderer とは完全に独立。
/// 完成ストロークはページ単位の Ink Texture (CanvasRenderTarget) に焼き、
/// 描画中ストロークはセグメント単位で直接描画する。
/// </summary>
public sealed class InkRenderer
{
    private sealed class PageTexture
    {
        public CanvasRenderTarget? Texture;
        public double PxPerPt;
        public bool Dirty;
    }

    /// <summary>テクスチャを再構築する表示スケールの相対差。</summary>
    private const double RebuildScaleTolerance = 0.25;

    /// <summary>Ink Texture の総ピクセル上限 (PDF と同じ予算)。</summary>
    private const long MaxTexturePixels = 16_000_000;

    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly Dictionary<int, PageTexture> _textures = new();
    private readonly Dictionary<int, (double WidthPt, double HeightPt)> _pageGeometry = new();
    private double _displayPxPerPt = RenderScale.DefaultPxPerPt;

    public void SetDisplayScale(double pxPerPt)
    {
        _displayPxPerPt = Math.Clamp(pxPerPt, RenderScale.MinPxPerPt, RenderScale.MaxPxPerPt);
    }

    public void SetPageGeometry(int pageIndex, double widthPt, double heightPt)
    {
        _pageGeometry[pageIndex] = (widthPt, heightPt);
    }

    /// <summary>ページの Ink Texture を破棄 (再構築は次回描画時)。</summary>
    public void MarkPageDirty(int pageIndex)
    {
        if (_textures.TryGetValue(pageIndex, out var entry))
        {
            entry.Texture?.Dispose();
            entry.Texture = null;
            entry.Dirty = true;
        }
    }

    public void MarkAllDirty()
    {
        foreach (var entry in _textures.Values)
        {
            entry.Texture?.Dispose();
            entry.Texture = null;
            entry.Dirty = true;
        }
    }

    public void RemovePage(int pageIndex)
    {
        if (_textures.TryGetValue(pageIndex, out var entry))
        {
            entry.Texture?.Dispose();
            _textures.Remove(pageIndex);
        }
        _pageGeometry.Remove(pageIndex);
    }

    public void Clear()
    {
        foreach (var entry in _textures.Values)
        {
            entry.Texture?.Dispose();
        }
        _textures.Clear();
        _pageGeometry.Clear();
    }

    /// <summary>
    /// 現在ページの Ink を描画する。session.Transform はページ pt 空間 (表示スケール) であること。
    /// </summary>
    public void Draw(CanvasDrawingSession session, InkPage? page, Stroke? activeStroke, int pageIndex)
    {
        var baseTransform = session.Transform;

        if (page is not null && page.StrokeCount > 0)
        {
            var entry = EnsureTexture(pageIndex, page);
            if (entry?.Texture is { } texture)
            {
                session.Transform = baseTransform * Matrix3x2.CreateScale((float)(1.0 / entry.PxPerPt));
                session.DrawImage(texture);
                session.Transform = baseTransform;
            }
        }

        if (activeStroke is not null)
        {
            DrawStrokeSegments(session, activeStroke);
        }
    }

    /// <summary>ストロークを Ink Texture に焼き込む (完成時)。</summary>
    public void BakeStroke(Stroke stroke)
    {
        if (stroke.Points.Count == 0)
        {
            return;
        }

        var page = stroke.PageIndex;
        if (!_textures.TryGetValue(page, out var entry))
        {
            entry = new PageTexture();
            _textures[page] = entry;
        }

        if (entry.Texture is null)
        {
            // 次の Draw で EnsureTexture が構築してから焼き込む。
            return;
        }

        using var drawingSession = entry.Texture.CreateDrawingSession();
        drawingSession.Transform = Matrix3x2.CreateScale((float)entry.PxPerPt);
        DrawStrokeSegments(drawingSession, stroke);
    }

    /// <summary>Ink Texture の生成/再構築 (表示スケールと Page 状態に基づく)。</summary>
    private PageTexture? EnsureTexture(int pageIndex, InkPage page)
    {
        if (!_pageGeometry.TryGetValue(pageIndex, out var geometry))
        {
            return null;
        }

        if (!_textures.TryGetValue(pageIndex, out var entry))
        {
            entry = new PageTexture();
            _textures[pageIndex] = entry;
        }

        var scaleChanged = Math.Abs(entry.PxPerPt / _displayPxPerPt - 1.0) > RebuildScaleTolerance;
        if (entry.Texture is null || entry.Dirty || scaleChanged)
        {
            entry.Texture?.Dispose();

            var pxPerPt = ClampToPixelBudget(geometry.WidthPt, geometry.HeightPt, _displayPxPerPt);
            var widthPx = Math.Max(1, (int)Math.Ceiling(geometry.WidthPt * pxPerPt));
            var heightPx = Math.Max(1, (int)Math.Ceiling(geometry.HeightPt * pxPerPt));

            var texture = new CanvasRenderTarget(_device, widthPx, heightPx, 96f);
            using (var drawingSession = texture.CreateDrawingSession())
            {
                drawingSession.Clear(Colors.Transparent);
                drawingSession.Transform = Matrix3x2.CreateScale((float)pxPerPt);
                foreach (var stroke in page.Strokes)
                {
                    DrawStrokeSegments(drawingSession, stroke);
                }
            }

            entry.Texture = texture;
            entry.PxPerPt = pxPerPt;
            entry.Dirty = false;
        }

        return entry;
    }


    /// <summary>
    /// ストロークをセグメント単位で描画する (pt 空間)。
    /// 筆圧→線幅は単純マッピング (将来カーブ設定可能にする)。
    /// </summary>
    private static void DrawStrokeSegments(CanvasDrawingSession session, Stroke stroke)
    {
        var points = stroke.Points;
        var count = points.Count;
        if (count == 0)
        {
            return;
        }

        var color = Color.FromArgb(stroke.A, stroke.R, stroke.G, stroke.B);
        if (count == 1)
        {
            session.FillCircle(points[0].Position, Math.Max(0.5f, stroke.WidthPt * 0.5f), color);
            return;
        }

        var previous = points[0];
        for (var i = 1; i < count; i++)
        {
            var current = points[i];
            var width = Math.Max(0.35f, stroke.WidthPt * PressureToWidthFactor(current.Pressure));
            session.DrawLine(previous.Position, current.Position, color, width);
            previous = current;
        }
    }

    /// <summary>筆圧 (0..1) → 線幅係数。0.4倍〜1.2倍。</summary>
    public static float PressureToWidthFactor(float pressure)
    {
        var p = Math.Clamp(pressure, 0f, 1f);
        return 0.4f + 0.8f * p;
    }

    private static double ClampToPixelBudget(double widthPt, double heightPt, double pxPerPt)
    {
        if (widthPt <= 0 || heightPt <= 0)
        {
            return pxPerPt;
        }

        var max = Math.Sqrt(MaxTexturePixels / (widthPt * heightPt));
        var scale = Math.Min(pxPerPt, max);
        return Math.Max(scale, Math.Min(RenderScale.MinPxPerPt, max));
    }
}

