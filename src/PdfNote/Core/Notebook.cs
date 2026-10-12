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

    /// <summary>
    /// ページ上のオブジェクト (PDF など)。Z 順 → リスト順で描画。
    /// ストローク (Ink) とは完全に独立したリストで、オブジェクト変形はここだけを変更する。
    /// </summary>
    public List<PageObject> Objects { get; } = new();
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

    /// <summary>
    /// PDF / 画像リソースの実体参照。実体は assets/ に 1 回だけ格納され、
    /// オブジェクトからは Id 参照される (メタデータへの重複埋め込みをしない)。
    /// </summary>
    public List<Resource> Resources { get; } = new();
}

/// <summary>PDF オブジェクト / ページ上オブジェクトの上限・許容値。</summary>
public static class PageObjectLimits
{
    /// <summary>1 ページあたりのオブジェクト数ソフト上限。超過時は UI が警告し、明示的な続行で解除。</summary>
    public const int MaxObjectsPerPage = 10;

    /// <summary>同時に開いたまま保持できる PDF リソース束の上限 (段階5手順3で使用)。</summary>
    public const int MaxResourceSessions = 4;

    /// <summary>リサイズ時の最小寸法 (pt)。0 以下や極端に小さい値を保存しない。</summary>
    public const float MinObjectSizePt = 20f;

    /// <summary>初期版で許可する回転角 (度)。</summary>
    public static readonly int[] AllowedRotations = { 0, 90, 180, 270 };

    public static bool IsAllowedRotation(int rotationDeg) =>
        Array.IndexOf(AllowedRotations, rotationDeg) >= 0;
}

/// <summary>PDF / 画像の外部リソース参照。実体はノートの assets/ にコピーされる (元ファイルは不変)。</summary>
public sealed class Resource
{
    public Resource(Guid id, string kind, string fileName, long fileSize, DateTime lastWriteTimeUtc)
    {
        Id = id;
        Kind = string.IsNullOrWhiteSpace(kind) ? "Pdf" : kind;
        FileName = fileName ?? string.Empty;
        FileSize = fileSize;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    /// <summary>リソース ID (オブジェクト ID / PDF 内ページ番号とは別物)。</summary>
    public Guid Id { get; }

    /// <summary>"Pdf" / "Image"。</summary>
    public string Kind { get; }

    /// <summary>ノートフォルダ起点の相対パス (例: assets/&lt;短ID&gt;.pdf)。</summary>
    public string FileName { get; }

    /// <summary>取り込み時の指紋 (照合用。v1 と同じ方針)。</summary>
    public long FileSize { get; }

    public DateTime LastWriteTimeUtc { get; }

    /// <summary>assets/ 内のファイル名に使う短 ID。</summary>
    public string ShortId => Id.ToString("N")[..8];
}

/// <summary>
/// ページ上に配置できるオブジェクトの基底。
/// X/Y/寸法/回転/Z はこのオブジェクト自身だけに適用され、
/// ストローク (NotebookPage.Ink, ページ pt 空間) には一切影響しない。
/// </summary>
public abstract class PageObject
{
    protected PageObject(Guid id, float x, float y, float widthPt, float heightPt, int rotationDeg, int z)
    {
        Id = id;
        X = x;
        Y = y;
        SetSize(widthPt, heightPt);
        RotationDeg = rotationDeg;
        Z = z;
    }

    /// <summary>オブジェクト ID (resourceId とは別物)。</summary>
    public Guid Id { get; }

    /// <summary>ページ左上原点 pt。回転前バウンディング矩形の左上。</summary>
    public float X { get; set; }

    public float Y { get; set; }

    /// <summary>ページ上の表示寸法 (pt、回転前)。</summary>
    public float WidthPt { get; private set; }

    public float HeightPt { get; private set; }

    /// <summary>回転角 (度)。初期版は 0/90/180/270 のみ。</summary>
    public int RotationDeg { get; private set; }

    /// <summary>Z 順序 (同値はリスト順)。</summary>
    public int Z { get; set; }

    public void SetSize(float widthPt, float heightPt)
    {
        if (!float.IsFinite(widthPt) || widthPt <= 0f || !float.IsFinite(heightPt) || heightPt <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPt), "オブジェクトの寸法は正の有限値である必要があります。");
        }

        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public void SetRotation(int rotationDeg)
    {
        if (!PageObjectLimits.IsAllowedRotation(rotationDeg))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDeg),
                $"回転角は {string.Join("/", PageObjectLimits.AllowedRotations)} のいずれかである必要があります。");
        }

        RotationDeg = rotationDeg;
    }
}

/// <summary>ページ上に配置された PDF ページ。独立オブジェクトとして移動 / 拡縮 / 回転 / Z 順変更が可能。</summary>
public sealed class PdfObject : PageObject
{
    public PdfObject(Guid id, Guid resourceId, int pdfPageIndex,
        float x, float y, float widthPt, float heightPt, int rotationDeg, int z)
        : base(id, x, y, widthPt, heightPt, rotationDeg, z)
    {
        ResourceId = resourceId;
        PdfPageIndex = pdfPageIndex;
    }

    /// <summary>参照先リソース (Notebook.Resources 内の Id)。</summary>
    public Guid ResourceId { get; }

    /// <summary>PDF 内ページ番号 (0 始まり)。</summary>
    public int PdfPageIndex { get; }
}
