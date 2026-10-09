using System;
using System.Collections.Generic;
using System.Numerics;

namespace PdfNote.Ink;

/// <summary>描画ツール種別。</summary>
public enum InkTool
{
    Pen,
    Eraser,
    Highlighter,
}

/// <summary>
/// ストロークの1点。value type のためポイント単位のオブジェクト生成は発生しない。
/// 座標はページ左上原点の pt 座標 (ズーム/パンに影響されない)。
/// </summary>
public readonly struct InkPoint
{
    public InkPoint(Vector2 position, float pressure)
    {
        Position = position;
        Pressure = pressure;
    }

    public Vector2 Position { get; }

    /// <summary>筆圧 (0..1)。</summary>
    public float Pressure { get; }
}

/// <summary>
/// 1本の手書き線。Phase 3 で .note にそのまま保存可能な構造。
/// 部分消去への将来拡張のため Points は生座標列を保持する。
/// </summary>
public sealed class Stroke
{
    /// <summary>筆圧→線幅の単純マッピングの既定パラメータ (将来カーブ設定可能にする)。</summary>
    public const float BaseWidthPt = 2.0f;

    public Stroke(int pageIndex, InkTool tool, float widthPt, byte r, byte g, byte b, byte a, long timestampTicks)
    {
        PageIndex = pageIndex;
        Tool = tool;
        WidthPt = widthPt;
        R = r;
        G = g;
        B = b;
        A = a;
        TimestampTicks = timestampTicks;
        Points = new List<InkPoint>(64);
    }

    public int PageIndex { get; }
    public InkTool Tool { get; }
    public float WidthPt { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }
    public long TimestampTicks { get; }
    public List<InkPoint> Points { get; }

    /// <summary>バウンディングボックス (pt)。ヒットテストの高速化用。</summary>
    public void GetBounds(out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        foreach (var p in Points)
        {
            if (p.Position.X < minX) minX = p.Position.X;
            if (p.Position.Y < minY) minY = p.Position.Y;
            if (p.Position.X > maxX) maxX = p.Position.X;
            if (p.Position.Y > maxY) maxY = p.Position.Y;
        }
    }
}

/// <summary>1ページ分の手書きデータ。</summary>
public sealed class InkPage
{
    public InkPage(int pageIndex)
    {
        PageIndex = pageIndex;
        Strokes = new List<Stroke>(64);
    }

    public int PageIndex { get; }
    public List<Stroke> Strokes { get; }

    public int StrokeCount => Strokes.Count;
}

/// <summary>開いているPDFの手書きデータ全体。ページ単位で管理する。</summary>
public sealed class InkDocument
{
    private readonly Dictionary<int, InkPage> _pages = new();

    /// <summary>すべてのページ (保存/スナップショット用)。</summary>
    public IReadOnlyCollection<InkPage> Pages => _pages.Values;

    public InkPage GetOrCreatePage(int pageIndex)
    {
        if (!_pages.TryGetValue(pageIndex, out var page))
        {
            page = new InkPage(pageIndex);
            _pages[pageIndex] = page;
        }

        return page;
    }

    /// <summary>
    /// 既存の InkPage インスタンスを外部から登録する。
    /// NotebookPage.Ink と同一インスタンスを共有するための追加 API (既存動作は不変)。
    /// </summary>
    public void AttachPage(int pageIndex, InkPage page) => _pages[pageIndex] = page;

    public bool HasStrokes(int pageIndex) =>
        _pages.TryGetValue(pageIndex, out var page) && page.StrokeCount > 0;

    public InkPage? TryGetPage(int pageIndex) =>
        _pages.TryGetValue(pageIndex, out var page) ? page : null;

    public void Clear() => _pages.Clear();
}
