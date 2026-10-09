using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using PdfNote.Core;
using PdfNote.Ink;

namespace PdfNote.Storage;

/// <summary>
/// Notebook (メモリモデル) と ノートブック JSON (v2) の境界。
/// 検証ルールと原子保存は NoteSerializer (v1) と同じ規約を用いる。
/// v1 (.note / NoteSerializer) は一切変更していない。
/// </summary>
public static class NotebookSerializer
{
    public const int CurrentFormatVersion = 2;

    /// <summary>1 ストロークあたりの最大点数 (破損/異常データ防御)。v1 と同じ。</summary>
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
    // メモリモデル → DTO (スナップショット)。呼び出しは UI スレッド想定。
    // ============================================================
    public static NotebookFileDto ToFile(Notebook notebook)
    {
        var pages = new List<NotebookPageFileDto>(notebook.Pages.Count);

        foreach (var page in notebook.Pages)
        {
            var strokes = new List<NoteStrokeDto>(page.Ink.Strokes.Count);
            foreach (var stroke in page.Ink.Strokes)
            {
                strokes.Add(ToStrokeDto(stroke));
            }

            pages.Add(new NotebookPageFileDto
            {
                PageIndex = page.PageIndex,
                WidthPt = page.WidthPt,
                HeightPt = page.HeightPt,
                Background = new NotebookBackgroundDto
                {
                    Kind = page.Background.ToString(),
                    GridSpacingPt = page.GridSpacingPt,
                },
                Strokes = strokes,
            });
        }

        return new NotebookFileDto
        {
            FormatVersion = CurrentFormatVersion,
            Notebook = new NotebookInfoDto
            {
                Id = notebook.Id.ToString("D"),
                Name = notebook.Name,
                CreatedUtc = notebook.CreatedUtc.ToString("o"),
                UpdatedUtc = notebook.UpdatedUtc.ToString("o"),
            },
            // 段階5 (PDF/画像オブジェクト) で使用する。空配列を予約しておく。
            Resources = [],
            Pages = pages,
        };
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
    // 保存 (原子保存: .tmp → Flush(flushToDisk) → File.Replace → .bak)。
    // 呼び出しはバックグラウンドスレッドを想定。
    // ============================================================
    public static void Save(string notebookPath, NotebookFileDto file)
    {
        var temporaryPath = notebookPath + ".tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, WriteOptions);
                stream.Flush(true); // ディスクまで書き込んでから置換する
            }

            if (File.Exists(notebookPath))
            {
                // File.Replace は同一ボリューム上で原子置換 + .bak に旧ファイル退避。
                File.Replace(temporaryPath, notebookPath, notebookPath + ".bak");
            }
            else
            {
                File.Move(temporaryPath, notebookPath);
            }
        }
        catch
        {
            // 保存失敗時は既存ファイルを破壊しない (tmp のみ掃除)。
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
    // 読み込み。
    // ============================================================
    public static NotebookFileDto ParseFile(string json)
    {
        var file = JsonSerializer.Deserialize<NotebookFileDto>(json, ReadOptions)
                   ?? throw new NoteLoadException("ノートブックファイルが空です。");

        if (file.FormatVersion is not { } version || version != CurrentFormatVersion)
        {
            throw new NoteLoadException(
                $"ノートブックファイルのバージョンが不明です (version={file.FormatVersion?.ToString() ?? "欠損"}, 対応={CurrentFormatVersion})。");
        }

        return file;
    }

    public static NotebookFileDto LoadFile(string notebookPath)
    {
        return ParseFile(File.ReadAllText(notebookPath));
    }

    /// <summary>
    /// DTO → メモリモデル。不正データはストローク/ページ単位で破棄し、クラッシュさせない。
    /// </summary>
    public static Notebook ToNotebook(NotebookFileDto file)
    {
        var info = file.Notebook;
        var id = Guid.TryParse(info?.Id, out var parsedId) ? parsedId : Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(info?.Name) ? "無題ノート" : info!.Name;
        var notebook = new Notebook(id, name);
        if (DateTime.TryParse(info?.CreatedUtc, out var created))
        {
            notebook.CreatedUtc = DateTime.SpecifyKind(created.ToUniversalTime(), DateTimeKind.Utc);
        }

        // 不正ページを除外してから PageIndex 順に整列 (重複 / 飛び番号はリスト順に正規化)。
        var pageDtos = new List<NotebookPageFileDto>();
        foreach (var dto in file.Pages ?? [])
        {
            if (dto is not null && dto.PageIndex >= 0 && dto.WidthPt > 0 && dto.HeightPt > 0)
            {
                pageDtos.Add(dto);
            }
        }

        pageDtos.Sort(static (a, b) => a.PageIndex.CompareTo(b.PageIndex));

        for (var i = 0; i < pageDtos.Count; i++)
        {
            var dto = pageDtos[i];
            var kind = Enum.TryParse<PageBackgroundKind>(dto.Background?.Kind, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : PageBackgroundKind.Plain;
            var spacing = dto.Background?.GridSpacingPt is double s && double.IsFinite(s) && s >= 2.0
                ? (float)s
                : NotebookDefaults.GridSpacingPt;

            // リスト位置 (= PageIndex) に正規化して生成。
            var page = new NotebookPage(i, dto.WidthPt, dto.HeightPt, kind)
            {
                GridSpacingPt = spacing,
            };

            foreach (var strokeDto in dto.Strokes ?? [])
            {
                if (strokeDto?.Points is not { } points || points.Length == 0)
                {
                    continue;
                }

                if (TryParseStroke(strokeDto, points, i) is { } stroke)
                {
                    page.Ink.Strokes.Add(stroke);
                }
            }

            notebook.Pages.Add(page);
        }

        return notebook;
    }

    /// <summary>ストロークの検証。不正な場合は null を返す (v1 と同じ規約)。</summary>
    private static Stroke? TryParseStroke(NoteStrokeDto dto, double[] points, int pageIndex)
    {
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

            strokePoints.Add(new InkPoint(new Vector2((float)x, (float)y), (float)pressure));
        }

        return strokePoints.Count > 0 ? stroke : null;
    }
}
