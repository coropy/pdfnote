using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using PdfNote.Core;

namespace PdfNote.Storage;

// ============================================================
// ノートブック保存形式 (JSON v2) の DTO (段階2 最小版)。
// pages + strokes のみを扱い、resources / objects は段階5で追加する。
// 大きな PDF / 画像データは一切埋め込まない (段階2では PDF 非対象)。
// 内部モデル (Notebook / InkPage) と完全に分離された境界。
// ============================================================

public sealed class NotebookFileDto
{
    /// <summary>フォーマットバージョン (= 2)。欠損は null → 不明バージョンとして拒否。</summary>
    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; set; }

    [JsonPropertyName("notebook")]
    public NotebookInfoDto? Notebook { get; set; }

    /// <summary>PDF / 画像リソース (段階5で使用)。段階2〜4では常に空。</summary>
    [JsonPropertyName("resources")]
    public List<NotebookResourceDto>? Resources { get; set; }

    [JsonPropertyName("pages")]
    public List<NotebookPageFileDto>? Pages { get; set; }
}

/// <summary>外部リソース参照 (実体は assets/ に 1 回だけ格納。メタデータに重複埋め込みしない)。</summary>
public sealed class NotebookResourceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>"Pdf" / "Image"。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "Pdf";

    /// <summary>assets/ 内の相対パス。</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("lastWriteTimeUtc")]
    public string LastWriteTimeUtc { get; set; } = string.Empty;
}

public sealed class NotebookInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>UTC (ISO 8601)。</summary>
    [JsonPropertyName("createdUtc")]
    public string CreatedUtc { get; set; } = string.Empty;

    [JsonPropertyName("updatedUtc")]
    public string UpdatedUtc { get; set; } = string.Empty;
}

public sealed class NotebookPageFileDto
{
    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }

    [JsonPropertyName("widthPt")]
    public double WidthPt { get; set; }

    [JsonPropertyName("heightPt")]
    public double HeightPt { get; set; }

    [JsonPropertyName("background")]
    public NotebookBackgroundDto? Background { get; set; }

    /// <summary>ストローク (v1 と同じ NoteStrokeDto 構造を再利用)。</summary>
    [JsonPropertyName("strokes")]
    public List<NoteStrokeDto>? Strokes { get; set; }

    /// <summary>
    /// ページ上のオブジェクト (段階5)。旧形式では欠落 (null → 空として扱う)。
    /// </summary>
    [JsonPropertyName("objects")]
    public List<NotebookObjectFileDto>? Objects { get; set; }
}

/// <summary>ページ上オブジェクトの DTO。kind で種別を判別する (段階5は "pdf" のみ)。</summary>
public sealed class NotebookObjectFileDto
{
    /// <summary>"pdf" (将来 "image" 等を追加予定。未知の種別は読み込み時に破棄)。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "pdf";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>ページ左上原点 pt (回転前バウンディング矩形の左上)。</summary>
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("widthPt")]
    public double WidthPt { get; set; }

    [JsonPropertyName("heightPt")]
    public double HeightPt { get; set; }

    /// <summary>0 / 90 / 180 / 270 のみ (それ以外は読み込み時に破棄)。</summary>
    [JsonPropertyName("rotationDeg")]
    public int RotationDeg { get; set; }

    [JsonPropertyName("z")]
    public int Z { get; set; }

    /// <summary>参照先リソース Id (resources 内に存在しない場合は破棄)。</summary>
    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; set; }

    /// <summary>PDF 内ページ番号 (0 始まり)。</summary>
    [JsonPropertyName("pdfPageIndex")]
    public int PdfPageIndex { get; set; }
}

public sealed class NotebookBackgroundDto
{
    /// <summary>"Plain" / "Grid"。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = nameof(PageBackgroundKind.Plain);

    /// <summary>方眼間隔 (pt)。Plain では未使用。</summary>
    [JsonPropertyName("gridSpacingPt")]
    public double GridSpacingPt { get; set; } = NotebookDefaults.GridSpacingPt;
}
