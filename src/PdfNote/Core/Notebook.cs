using System;
using System.Collections.Generic;
using PdfNote.Ink;

namespace PdfNote.Core;

/// <summary>ページ背景の種別。段階2では白紙 / 方眼のみ。</summary>
public enum PageBackgroundKind
{
    /// <summary>白紙。</summary>
    Plain,

    /// <summary>方眼 (<see cref="NotebookDefaults.GridSpacingPt"/> 間隔)。</summary>
    Grid,
}

/// <summary>既定のページ寸法・背景パラメータ。段階2は A4 縦固定。</summary>
public static class NotebookDefaults
{
    /// <summary>A4 縦の幅 (pt = 1/72 inch)。</summary>
    public const double A4WidthPt = 595.276;

    /// <summary>A4 縦の高さ (pt)。</summary>
    public const double A4HeightPt = 841.89;

    /// <summary>方眼の間隔 (pt)。5mm。</summary>
    public const float GridSpacingPt = 14.1732f;

    /// <summary>白紙ノート新規作成時のページ数。</summary>
    public const int DefaultPageCount = 10;
}

/// <summary>
/// 1ページ分。寸法・背景設定・手書きストロークを保持する。
/// ストロークは既存 Ink モデル (<see cref="InkPage"/>) をそのまま利用し、
/// InkSession 側の InkDocument と同一インスタンスを共有する (状態の二重化を避ける)。
/// 座標はページ左上原点の pt。
/// </summary>
public sealed class NotebookPage
{
    public NotebookPage(int pageIndex, double widthPt, double heightPt, PageBackgroundKind background)
    {
        PageIndex = pageIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
        Background = background;
        Ink = new InkPage(pageIndex);
    }

    /// <summary>表示順 = Notebook.Pages リストの順序。InkPage.PageIndex と一致する。</summary>
    public int PageIndex { get; }

    public double WidthPt { get; }

    public double HeightPt { get; }

    public PageBackgroundKind Background { get; set; }

    /// <summary>方眼間隔 (pt)。</summary>
    public float GridSpacingPt { get; set; } = NotebookDefaults.GridSpacingPt;

    /// <summary>このページの手書きストローク (Ink モデルをそのまま利用)。</summary>
    public InkPage Ink { get; }
}

/// <summary>ノートブック本体。PDF とは独立して複数ページを保持する。</summary>
public sealed class Notebook
{
    public Notebook(Guid id, string name)
    {
        Id = id;
        Name = name;
        CreatedUtc = DateTime.UtcNow;
        UpdatedUtc = CreatedUtc;
    }

    public Guid Id { get; }

    public string Name { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>ページ一覧。表示順 = リスト順。</summary>
    public List<NotebookPage> Pages { get; } = new();
}
