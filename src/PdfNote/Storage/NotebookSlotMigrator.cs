using System;
using System.Collections.Generic;
using System.IO;
using PdfNote.Core;

namespace PdfNote.Storage;

/// <summary>
/// 段階2の暫定形式 (Notebooks 直下のフラット *.nb.json) を
/// &lt;名前&gt;-&lt;短ID&gt;/notebook.json + assets/ へ非破壊移行する。
/// コピー → 検証 (Id / ページ数 / ストローク総数) → 元ファイルを *.migrated に改名 (削除しない)。
/// </summary>
public static class NotebookSlotMigrator
{
    /// <summary>移行済み旧ファイルの接尾辞。</summary>
    public const string MigratedSuffix = ".migrated";

    /// <summary>ルート直下の未移行ファイルを移行する。戻り値は移行成功数。</summary>
    public static int MigratePending(string root)
    {
        var migrated = 0;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.nb.json"))
        {
            if (path.EndsWith(MigratedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var file = NotebookSerializer.LoadFile(path);
                var notebook = NotebookSerializer.ToNotebook(file);

                var folder = NotebookLibrary.Create(root, string.IsNullOrWhiteSpace(notebook.Name) ? "無題ノート" : notebook.Name, notebook);
                if (!Verify(folder, notebook))
                {
                    // 検証失敗: 作成したフォルダを破棄し、旧ファイルはそのまま残す。
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch
                    {
                        // 削除失敗は無視 (次回起動時に再試行)。
                    }

                    continue;
                }

                // 元ファイルは削除せず改名する (ユーザーデータ保全)。
                File.Move(path, path + MigratedSuffix);
                migrated++;
            }
            catch
            {
                // 破損 / IO 失敗のファイルはスキップ (削除はしない)。
            }
        }

        return migrated;
    }

    /// <summary>移行先ファイルを再読み込みし、ページ数とストローク総数が一致するか確認する。</summary>
    private static bool Verify(string folder, Notebook original)
    {
        try
        {
            var jsonPath = Path.Combine(folder, NotebookLibrary.NotebookFileName);
            var restored = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(jsonPath));
            if (restored.Id != original.Id)
            {
                return false;
            }

            if (restored.Pages.Count != original.Pages.Count)
            {
                return false;
            }

            for (var i = 0; i < restored.Pages.Count; i++)
            {
                if (restored.Pages[i].Ink.Strokes.Count != original.Pages[i].Ink.Strokes.Count)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
