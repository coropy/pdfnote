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

    /// <summary>所属フォルダのディレクトリ名 (ルートは空文字)。</summary>
    public string Folder { get; init; } = string.Empty;

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
    /// folder は所属フォルダのディレクトリ名 (ルートは null / 空文字)。
    /// notebook.Name は表示名で更新される (Id は呼び出し側で確定済みであること)。
    /// </summary>
    public static string Create(string root, string displayName, Notebook notebook, string? folder = null)
    {
        Directory.CreateDirectory(root);
        var parent = GetFolderDirectory(root, folder);
        notebook.Name = string.IsNullOrWhiteSpace(displayName) ? "無題ノート" : displayName.Trim();
        notebook.UpdatedUtc = DateTime.UtcNow;

        var baseName = $"{SanitizeFolderName(notebook.Name)}-{ShortId(notebook.Id)}";
        var folderPath = Path.Combine(parent, baseName);
        for (var i = 2; Directory.Exists(folderPath); i++)
        {
            folderPath = Path.Combine(parent, $"{baseName}-{i}");
        }

        Directory.CreateDirectory(folderPath);
        Directory.CreateDirectory(Path.Combine(folderPath, "assets"));
        NotebookSerializer.Save(Path.Combine(folderPath, NotebookFileName), NotebookSerializer.ToFile(notebook));
        return folderPath;
    }

    /// <summary>フォルダ (ディレクトリ) の実パスを解決する (ルートは root 自身)。</summary>
    private static string GetFolderDirectory(string root, string? folder) =>
        string.IsNullOrWhiteSpace(folder)
            ? root
            : Path.Combine(root, SanitizeFolderName(folder));

    /// <summary>
    /// ノートブック一覧 (最近更新順)。破損フォルダは IsBroken=true で表示する (隠さない)。
    /// 構成はルート直下 or 1 階層のフォルダ (<root>/<フォルダ>/<ノート>)。2 階層以上は無視。
    /// 旧形式のフラットファイル (*.nb.json) は無視する (NotebookSlotMigrator が扱う)。
    /// </summary>
    public static IReadOnlyList<NotebookSummary> List(string root)
    {
        var summaries = new List<NotebookSummary>();
        if (!Directory.Exists(root))
        {
            return summaries;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var dirName = Path.GetFileName(dir);
            if (File.Exists(Path.Combine(dir, NotebookFileName)))
            {
                AddSummary(summaries, dir, string.Empty);
                continue;
            }

            // ノートブックを持たないディレクトリはフォルダ。直下のノートを収集する。
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (File.Exists(Path.Combine(sub, NotebookFileName)))
                {
                    AddSummary(summaries, sub, dirName);
                }
            }
        }

        summaries.Sort(static (a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        return summaries;
    }

    /// <summary>1 つのノートブックフォルダから NotebookSummary を生成して追加する。</summary>
    private static void AddSummary(List<NotebookSummary> summaries, string noteDirectory, string folder)
    {
        var jsonPath = Path.Combine(noteDirectory, NotebookFileName);
        var folderName = Path.GetFileName(noteDirectory);
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
                FolderPath = noteDirectory,
                FolderName = folderName,
                Folder = folder,
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
                FolderPath = noteDirectory,
                FolderName = folderName,
                Folder = folder,
                Name = "(破損)",
                UpdatedUtc = updated,
                IsBroken = true,
                Error = ex.Message,
            });
        }
    }

    /// <summary>
    /// フォルダ一覧 (<root> 直下のノートブックを持たないディレクトリ名)。名前順。
    /// 空フォルダも含まれる (物理ディレクトリとして存在するため)。
    /// </summary>
    public static IReadOnlyList<string> ListFolders(string root)
    {
        var folders = new List<string>();
        if (!Directory.Exists(root))
        {
            return folders;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, NotebookFileName)))
            {
                folders.Add(Path.GetFileName(dir));
            }
        }

        folders.Sort(StringComparer.OrdinalIgnoreCase);
        return folders;
    }

    /// <summary>新規フォルダを作成する (同名時は "名前 (1)", "名前 (2)" ...)。戻り値は確定したディレクトリ名。</summary>
    public static string CreateFolder(string root, string displayName)
    {
        Directory.CreateDirectory(root);
        var baseName = SanitizeFolderName(displayName);
        var target = Path.Combine(root, baseName);
        for (var i = 2; Directory.Exists(target); i++)
        {
            target = Path.Combine(root, $"{baseName} ({i})");
        }

        Directory.CreateDirectory(target);
        return Path.GetFileName(target);
    }

    /// <summary>フォルダ名を変更する (同名時は "名前 (1)", ... を付加)。戻り値は確定したディレクトリ名。</summary>
    public static string RenameFolder(string root, string folder, string newName)
    {
        var source = Path.Combine(root, SanitizeFolderName(folder));
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"フォルダが見つかりません: {source}");
        }

        var baseName = SanitizeFolderName(newName);
        var target = Path.Combine(root, baseName);
        if (!string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 2; Directory.Exists(target); i++)
            {
                target = Path.Combine(root, $"{baseName} ({i})");
            }

            Directory.Move(source, target);
        }

        return Path.GetFileName(target);
    }

    /// <summary>
    /// フォルダと中身 (ノートブック) をすべて削除する (呼び出し側で確認ダイアログを表示すること)。
    /// 開いているノートが含まれる場合は呼び出し側で先にデタッチすること。
    /// </summary>
    public static void DeleteFolder(string root, string folder)
    {
        var target = Path.Combine(root, SanitizeFolderName(folder));
        if (!Directory.Exists(target))
        {
            return;
        }

        Directory.Delete(target, recursive: true);
    }

    /// <summary>
    /// ノートブックを別のフォルダへ移動する。targetFolder はディレクトリ名 (ルートは null / 空文字)。
    /// 戻り値は移動後のノートブックフォルダ。
    /// </summary>
    public static string Move(string folderPath, string? targetFolder)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"ノートブックフォルダが見つかりません: {folderPath}");
        }

        var root = Path.GetDirectoryName(folderPath)
            ?? throw new InvalidOperationException($"不正なパスです: {folderPath}");
        var parent = GetFolderDirectory(root, targetFolder);
        Directory.CreateDirectory(parent);

        var baseName = Path.GetFileName(folderPath);
        var destination = Path.Combine(parent, baseName);
        for (var i = 2; Directory.Exists(destination); i++)
        {
            destination = Path.Combine(parent, $"{baseName}-{i}");
        }

        Directory.Move(folderPath, destination);
        return destination;
    }

    /// <summary>
    /// 既存ノートブックの表示名と重複しない名前を返す。
    /// baseName が既に使われている場合は "baseName (1)", "baseName (2)" ... を付加する。
    /// excludeId は自分自身 (名前変更対象) を重複判定から除外する。folder は同じフォルダ内のみ比較。
    /// </summary>
    public static string EnsureUniqueDisplayName(string root, string displayName, Guid? excludeId = null, string? folder = null)
    {
        var baseName = string.IsNullOrWhiteSpace(displayName) ? "無題ノート" : displayName.Trim();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in List(root))
        {
            if (excludeId is not null && summary.Id == excludeId)
            {
                continue;
            }

            if (!string.Equals(summary.Folder, folder ?? string.Empty, StringComparison.Ordinal))
            {
                continue;
            }

            existing.Add(summary.Name);
        }

        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 1; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
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

    // ------------------------------------------------------------
    // PDF 資産の取り込み (段階5 ステップ2)。
    // 元ファイルは読み取りのみで、変更 / 移動 / 削除は一切行わない。
    // 実体は <notebookFolder>/assets/<リソース短ID>.pdf に 1 回だけコピーする。
    // 指紋 (FileSize / LastWriteTimeUtc) は「変更検知」用であり、内容の同一性を保証しない。
    // ------------------------------------------------------------

    /// <summary>
    /// PDF をノートの assets/ へコピーしてリソース登録情報を返す。
    /// 保存名はリソース ID 由来のため、同名 PDF の再取り込みでも既存ファイルを上書きしない。
    /// コピー後の検証 (存在 / サイズ一致) に失敗した場合は部分コピーを削除して例外を再スローする
    /// (不完全なリソース登録は呼び出し側に残らない)。
    /// </summary>
    /// <param name="notebookFolder">ノートブックフォルダ。</param>
    /// <param name="sourcePath">取り込む PDF のパス (読み取り専用)。</param>
    /// <param name="kind">リソース種別 ("Pdf")。</param>
    /// <param name="resourceId">リソース Id (テスト用。通常は null = 新規生成)。</param>
    public static Resource AddResource(string notebookFolder, string sourcePath, string kind = "Pdf", Guid? resourceId = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"取り込むファイルが見つかりません: {sourcePath}");
        }

        var id = resourceId ?? Guid.NewGuid();
        var assetsDirectory = Path.Combine(notebookFolder, "assets");
        Directory.CreateDirectory(assetsDirectory);

        var baseName = $"{id.ToString("N")[..8]}.pdf";
        var target = Path.Combine(assetsDirectory, baseName);
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(assetsDirectory, $"{id.ToString("N")[..8]}-{i}.pdf");
        }

        try
        {
            File.Copy(sourcePath, target, overwrite: false);
        }
        catch
        {
            // 部分コピーが残らないようにする (失敗理由は呼び出し側へ通知)。
            TryDeleteFile(target);
            throw;
        }

        // コピー検証: 存在とサイズ一致。失敗時は不完全な資産を残さない。
        var sourceInfo = new FileInfo(sourcePath);
        var targetInfo = new FileInfo(target);
        if (!targetInfo.Exists || targetInfo.Length != sourceInfo.Length)
        {
            TryDeleteFile(target);
            throw new IOException($"PDF のコピー検証に失敗しました: {sourcePath} → {target}");
        }

        // FileName はノートフォルダ起点の相対パスのみを保存する (絶対パス / 一時パスは保存しない)。
        return new Resource(
            id,
            kind,
            Path.Combine("assets", Path.GetFileName(target)),
            targetInfo.Length,
            targetInfo.LastWriteTimeUtc);
    }

    /// <summary>
    /// リソースの実体ファイルを解決する。相対パスはノートフォルダ基準。
    /// ファイルが存在しない (参照切れ) 場合は null を返す (呼び出し側はプレースホルダ表示)。
    /// </summary>
    public static string? FindResourceFile(string notebookFolder, Resource resource)
    {
        if (string.IsNullOrWhiteSpace(resource.FileName) || Path.IsPathRooted(resource.FileName))
        {
            return null;
        }

        var fullPath = Path.Combine(notebookFolder, resource.FileName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 削除失敗は無視 (孤立ファイルは次回起動時に assets 掃除対象になり得るが、現状は放置)。
        }
    }
}
