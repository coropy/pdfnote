using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PdfNote.Core;

namespace PdfNote.Storage;

/// <summary>ノートブック一覧の1項目。</summary>
public sealed class NotebookSummary
{
    public required string FolderPath { get; init; }

    public required string FolderName { get; init; }

    /// <summary>notebook.json 内のノート Id (正)。破損時は null。</summary>
    public Guid? Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DateTime UpdatedUtc { get; init; }

    public int PageCount { get; init; }

    public int StrokeCount { get; init; }

    /// <summary>notebook.json が読めない (破損)。一覧には表示するが開けない。</summary>
    public bool IsBroken { get; init; }

    public string? Error { get; init; }

    /// <summary>一覧表示用 (更新日時)。</summary>
    public string UpdatedText => UpdatedUtc == default
        ? "更新日時不明"
        : $"更新 {UpdatedUtc.ToLocalTime():yyyy/MM/dd HH:mm}";

    /// <summary>一覧表示用 (詳細)。</summary>
    public string DetailText => IsBroken
        ? $"破損: {Error}"
        : $"ページ {PageCount} / ストローク {StrokeCount} · {FolderName}";
}

/// <summary>
/// ノートブックのライブラリ (ファイルシステム操作のみ。UI / 保存状態を知らない)。
/// 構成: &lt;root&gt;/&lt;サニタイズ済み名&gt;-&lt;短ID&gt;/notebook.json + assets/
/// ノートの識別は notebook.json 内の Id (GUID) が正で、フォルダ名は表示用。
/// </summary>
public static class NotebookLibrary
{
    public const string NotebookFileName = "notebook.json";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfNote",
        "Notebooks");

    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>フォルダ名に使えない文字を置換し、長すぎる名前を切り詰める。</summary>
    public static string SanitizeFolderName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name?.Length ?? 0);
        foreach (var c in string.IsNullOrWhiteSpace(name) ? "無題ノート" : name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var text = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(text))
        {
            text = "無題ノート";
        }

        if (text.Length > 40)
        {
            text = text[..40].Trim().TrimEnd('.');
        }

        return string.IsNullOrEmpty(text) ? "無題ノート" : text;
    }

    /// <summary>
    /// 新規ノートブックを作成する (notebook.json + assets/)。戻り値はノートブックフォルダ。
    /// notebook.Name は表示名で更新される (Id は呼び出し側で確定済みであること)。
    /// </summary>
    public static string Create(string root, string displayName, Notebook notebook)
    {
        Directory.CreateDirectory(root);
        notebook.Name = string.IsNullOrWhiteSpace(displayName) ? "無題ノート" : displayName.Trim();
        notebook.UpdatedUtc = DateTime.UtcNow;

        var baseName = $"{SanitizeFolderName(notebook.Name)}-{ShortId(notebook.Id)}";
        var folder = Path.Combine(root, baseName);
        for (var i = 2; Directory.Exists(folder); i++)
        {
            folder = Path.Combine(root, $"{baseName}-{i}");
        }

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        NotebookSerializer.Save(Path.Combine(folder, NotebookFileName), NotebookSerializer.ToFile(notebook));
        return folder;
    }

    /// <summary>
    /// ノートブック一覧 (最近更新順)。破損フォルダは IsBroken=true で表示する (隠さない)。
    /// 旧形式のフラットファイル (*.nb.json) は無視する (NotebookSlotMigrator が扱う)。
    /// </summary>
    public static IReadOnlyList<NotebookSummary> List(string root)
    {
        var summaries = new List<NotebookSummary>();
        if (!Directory.Exists(root))
        {
            return summaries;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var jsonPath = Path.Combine(folder, NotebookFileName);
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            var folderName = Path.GetFileName(folder);
            try
            {
                var file = NotebookSerializer.LoadFile(jsonPath);
                var id = Guid.TryParse(file.Notebook?.Id, out var parsed) ? parsed : (Guid?)null;
                var pages = 0;
                var strokes = 0;
                foreach (var page in file.Pages ?? [])
                {
                    pages++;
                    strokes += page?.Strokes?.Count ?? 0;
                }

                var updated = DateTime.TryParse(file.Notebook?.UpdatedUtc, out var u) ? u : DateTime.MinValue;
                summaries.Add(new NotebookSummary
                {
                    FolderPath = folder,
                    FolderName = folderName,
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(file.Notebook?.Name) ? "(無題)" : file.Notebook!.Name,
                    UpdatedUtc = updated,
                    PageCount = pages,
                    StrokeCount = strokes,
                });
            }
            catch (Exception ex)
            {
                DateTime updated;
                try
                {
                    updated = File.GetLastWriteTimeUtc(jsonPath);
                }
                catch
                {
                    updated = DateTime.MinValue;
                }

                summaries.Add(new NotebookSummary
                {
                    FolderPath = folder,
                    FolderName = folderName,
                    Name = "(破損)",
                    UpdatedUtc = updated,
                    IsBroken = true,
                    Error = ex.Message,
                });
            }
        }

        summaries.Sort(static (a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        return summaries;
    }

    /// <summary>Id でノートブックを解決する (フォルダ名は参考にしない)。</summary>
    public static (Notebook Notebook, string FolderPath)? FindById(string root, Guid id)
    {
        foreach (var summary in List(root))
        {
            if (summary.Id == id && !summary.IsBroken)
            {
                var jsonPath = Path.Combine(summary.FolderPath, NotebookFileName);
                return (NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(jsonPath)), summary.FolderPath);
            }
        }

        return null;
    }

    /// <summary>
    /// 表示名を変更する。まず notebook.json の Name を原子保存し、その後フォルダ名の変更を試みる。
    /// フォルダ名の変更に失敗しても (ロック等)、json 内の Name と Id は一致したまま (正は Id)。
    /// 戻り値は最終的なノートブックフォルダ。
    /// </summary>
    public static string Rename(string folderPath, string newName)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"ノートブックフォルダが見つかりません: {folderPath}");
        }

        var jsonPath = Path.Combine(folderPath, NotebookFileName);
        var file = NotebookSerializer.LoadFile(jsonPath);
        var id = Guid.TryParse(file.Notebook?.Id, out var parsed) ? parsed : Guid.NewGuid();
        var info = file.Notebook ?? new NotebookInfoDto();
        info.Name = string.IsNullOrWhiteSpace(newName) ? "無題ノート" : newName.Trim();
        info.UpdatedUtc = DateTime.UtcNow.ToString("o");
        file.Notebook = info;
        NotebookSerializer.Save(jsonPath, file);

        var root = Path.GetDirectoryName(folderPath);
        if (string.IsNullOrEmpty(root))
        {
            return folderPath;
        }

        var target = Path.Combine(root, $"{SanitizeFolderName(info.Name)}-{ShortId(id)}");
        if (string.Equals(target, folderPath, StringComparison.OrdinalIgnoreCase) || Directory.Exists(target))
        {
            return folderPath;
        }

        try
        {
            Directory.Move(folderPath, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ロック等でフォルダ名を変えられない場合は json の Name のみ更新済みで続行。
            return folderPath;
        }
    }

    /// <summary>ノートブックフォルダとその内容を削除する (呼び出し側で確認ダイアログを表示すること)。</summary>
    public static void Delete(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        Directory.Delete(folderPath, recursive: true);
    }
}
