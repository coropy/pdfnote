using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PdfNote.Ink;

namespace PdfNote.Storage;

/// <summary>ノートの読み込みに失敗した場合の例外。</summary>
public sealed class NoteLoadException : Exception
{
    public NoteLoadException(string message) : base(message) { }
}

/// <summary>
/// InkDocument (メモリモデル) と .note (JSON v1) の境界。
/// シリアライズ/デシリアライズと整合性検証のみを担う。
/// </summary>
public static class NoteSerializer
{
    public const int CurrentFormatVersion = 1;

    /// <summary>1 ストロークあたりの最大点数 (破損/異常データ防御)。</summary>
    private const int MaxPointsPerStroke = 100_000;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ============================================================
    // メモリモデル → DTO (スナップショット)。UI から離れたタイミングで
    // 呼ばれることを想定 (呼び出し側で排他)。
    // ============================================================
    public static NoteDocumentDto ToDocument(InkDocument document)
    {
        var pages = new List<NotePageDto>();

        foreach (var page in document.Pages)
        {
            var strokes = new List<NoteStrokeDto>(page.Strokes.Count);
            foreach (var stroke in page.Strokes)
            {
                strokes.Add(ToStrokeDto(stroke));
            }

            pages.Add(new NotePageDto { PageIndex = page.PageIndex, Strokes = strokes });
        }

        return new NoteDocumentDto { Pages = pages };
    }

    public static NoteStrokeDto ToStrokeDto(Stroke stroke)
    {
        var points = stroke.Points;
        var flat = new double[points.Count * 3];
        for (var i = 0; i < points.Count; i++)
        {
            flat[i * 3] = points[i].Position.X;
            flat[i * 3 + 1] = points[i].Position.Y;
            flat[i * 3 + 2] = points[i].Pressure;
        }

        return new NoteStrokeDto
        {
            Tool = stroke.Tool.ToString(),
            Width = stroke.WidthPt,
            ColorArgb = (stroke.A << 24) | (stroke.R << 16) | (stroke.G << 8) | stroke.B,
            CreatedTicks = stroke.TimestampTicks,
            Points = flat,
        };
    }

    // ============================================================
    // 保存 (原子保存)。呼び出しはバックグラウンドスレッドを想定。
    // ============================================================
    public static void Save(string notePath, NoteDocumentDto document, PdfInfoDto pdfInfo)
    {
        var file = new NoteFileDto
        {
            FormatVersion = CurrentFormatVersion,
            Pdf = pdfInfo,
            Document = document,
        };

        var temporaryPath = notePath + ".tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, WriteOptions);
                stream.Flush(true); // ディスクまで書き込んでから置換する
            }

            if (File.Exists(notePath))
            {
                // File.Replace は同一ボリューム上で原子置換 + .bak に旧ファイル退避。
                File.Replace(temporaryPath, notePath, notePath + ".bak");
            }
            else
            {
                File.Move(temporaryPath, notePath);
            }
        }
        catch
        {
            // 途中失敗時は一時ファイルを掃除して再スロー (既存 .note は破壊しない)。
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 掃除失敗は無視。
            }

            throw;
        }
    }


    // ============================================================
    // 読み込み + 整合性検証。
    // ============================================================
    public static NoteFileDto ParseFile(string json)
    {
        NoteFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<NoteFileDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new NoteLoadException($"ノートファイルが壊れています ({ex.Message})。");
        }

        if (file is null)
        {
            throw new NoteLoadException("ノートファイルの内容が空です。");
        }

        if (file.FormatVersion is not { } version || version != CurrentFormatVersion)
        {
            throw new NoteLoadException(
                $"ノートファイルのバージョンが不明です (version={file.FormatVersion?.ToString() ?? "欠損"}, 対応={CurrentFormatVersion})。");
        }

        return file;
    }

    public static NoteFileDto LoadFile(string notePath)
    {
        return ParseFile(File.ReadAllText(notePath));
    }

    /// <summary>
    /// DTO → メモリモデル。不正データはストローク/ページ単位で破棄し、クラッシュさせない。
    /// </summary>
    public static InkDocument ToInkDocument(NoteFileDto file, int currentPdfPageCount)
    {
        var document = new InkDocument();
        var pages = file.Document?.Pages;
        if (pages is null)
        {
            return document;
        }

        foreach (var page in pages)
        {
            if (page is null || page.Strokes is null)
            {
                continue;
            }

            // ページ範囲外のデータは破棄 (PDF 差し替え等で起こり得る)。
            if (page.PageIndex < 0 || page.PageIndex >= currentPdfPageCount)
            {
                continue;
            }

            var target = document.GetOrCreatePage(page.PageIndex);
            foreach (var stroke in page.Strokes)
            {
                if (stroke?.Points is not { } points || points.Length == 0)
                {
                    continue;
                }

                var parsed = TryParseStroke(stroke, points, page.PageIndex);
                if (parsed is not null)
                {
                    target.Strokes.Add(parsed);
                }
            }
        }

        return document;
    }

    /// <summary>ストロークの検証。不正な場合は null を返す (クラッシュさせない)。</summary>
    private static Stroke? TryParseStroke(NoteStrokeDto dto, double[] points, int pageIndex)
    {
        // 点列は [x, y, pressure] の 3 つ組。
        if (points.Length < 3 || points.Length % 3 != 0 || points.Length > MaxPointsPerStroke * 3)
        {
            return null;
        }

        var width = (float)dto.Width;
        if (!float.IsFinite(width) || width < 0.1f || width > 50f)
        {
            return null;
        }

        var color = dto.ColorArgb;
        if (!Enum.TryParse(dto.Tool, ignoreCase: true, out InkTool tool))
        {
            tool = InkTool.Pen;
        }

        var stroke = new Stroke(
            pageIndex,
            tool,
            width,
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF),
            (byte)((color >> 24) & 0xFF),
            dto.CreatedTicks);

        var strokePoints = stroke.Points;
        for (var i = 0; i + 2 < points.Length; i += 3)
        {
            var x = points[i];
            var y = points[i + 1];
            var pressure = points[i + 2];

            // 座標が NaN / Infinity のストロークは破損扱いで破棄。
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(pressure))
            {
                return null;
            }

            if (pressure < 0f)
            {
                pressure = 0f;
            }
            else if (pressure > 1f)
            {
                pressure = 1f;
            }

            strokePoints.Add(new InkPoint(new System.Numerics.Vector2((float)x, (float)y), (float)pressure));
        }

        if (strokePoints.Count == 0)
        {
            return null;
        }

        return stroke;
    }
}

