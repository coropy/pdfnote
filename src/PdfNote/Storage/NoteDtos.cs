using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PdfNote.Storage;

// ============================================================
// .note ファイル形式 (JSON v1) の DTO。
// アプリ内部モデル (InkDocument) と完全に分離されており、
// 将来バイナリ/圧縮形式へ差し替え可能な境界。
// ============================================================

public sealed class NoteFileDto
{
    /// <summary>フォーマットバージョン。欠損は null → 不明バージョンとして拒否。</summary>
    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; set; }

    [JsonPropertyName("pdf")]
    public PdfInfoDto? Pdf { get; set; }

    [JsonPropertyName("document")]
    public NoteDocumentDto? Document { get; set; }
}

/// <summary>対応する PDF の関連付け情報。</summary>
public sealed class PdfInfoDto
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("lastWriteTimeUtc")]
    public string LastWriteTimeUtc { get; set; } = string.Empty;

    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }
}

public sealed class NoteDocumentDto
{
    [JsonPropertyName("pages")]
    public List<NotePageDto>? Pages { get; set; }
}

public sealed class NotePageDto
{
    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }

    [JsonPropertyName("strokes")]
    public List<NoteStrokeDto>? Strokes { get; set; }
}

public sealed class NoteStrokeDto
{
    /// <summary>ツール種別 (Pen / Eraser / Highlighter)。</summary>
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "Pen";

    /// <summary>基本線幅 (pt)。</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    /// <summary>色 (ARGB パック値)。</summary>
    [JsonPropertyName("color")]
    public int ColorArgb { get; set; }

    /// <summary>作成日時 (DateTime.Ticks / UTC 基準は作成時の現地時刻)。</summary>
    [JsonPropertyName("createdTicks")]
    public long CreatedTicks { get; set; }

    /// <summary>点列のフラット配列 [x, y, pressure, ...] (ページpt座標)。</summary>
    [JsonPropertyName("points")]
    public double[]? Points { get; set; }
}
