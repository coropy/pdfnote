using System.Security.Cryptography;
using PdfNote.Core;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ2: PDF 資産取り込みの検証 (元ファイル保全 / 上書き防止)。</summary>
internal static class PdfResourceImportTests
{
    internal static void ImportPreservesSourceHash(Action<bool, string> check)
    {
        Console.WriteLine("PdfResourceImport_PreservesSourceHash");
        var root = NotebookLibraryTests.NewTempRoot();
        try
        {
            var source = Path.Combine(root, "source.pdf");
            var payload = new byte[4096];
            Random.Shared.NextBytes(payload);
            File.WriteAllBytes(source, payload);

            var hashBefore = Sha256(source);
            var folder = NotebookLibrary.Create(root, "取り込みテスト", new Notebook(Guid.NewGuid(), "n"));
            var resource = NotebookLibrary.AddResource(folder, source);

            check(Sha256(source).AsSpan().SequenceEqual(hashBefore), "元ファイルの SHA-256 は不変");
            check(File.Exists(source), "元ファイルは残る");

            var assetPath = NotebookLibrary.FindResourceFile(folder, resource);
            check(assetPath is not null, "assets へのコピー");
            check(new FileInfo(assetPath!).Length == payload.Length, "コピーのサイズ一致");
            check(resource.FileName.StartsWith("assets") && !Path.IsPathRooted(resource.FileName),
                "FileName はノート起点の相対パス");
            check(resource.FileName.Contains(Path.GetFileName(assetPath!)), "FileName が実ファイルと対応");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static void ImportTwiceDoesNotOverwrite(Action<bool, string> check)
    {
        Console.WriteLine("PdfResourceImport_TwiceDoesNotOverwrite");
        var root = NotebookLibraryTests.NewTempRoot();
        try
        {
            var source = Path.Combine(root, "same-name.pdf");
            File.WriteAllBytes(source, new byte[256]);
            var folder = NotebookLibrary.Create(root, "重複取り込み", new Notebook(Guid.NewGuid(), "n"));

            var r1 = NotebookLibrary.AddResource(folder, source);
            var r2 = NotebookLibrary.AddResource(folder, source);

            check(r1.Id != r2.Id, "リソース Id は毎回新規");
            check(r1.FileName != r2.FileName, "保存名はリソース Id 由来で別ファイル");
            check(File.Exists(Path.Combine(folder, r1.FileName)) &&
                  File.Exists(Path.Combine(folder, r2.FileName)), "両方の資産が存在 (上書きなし)");
            check(new FileInfo(Path.Combine(folder, r1.FileName)).Length == 256 &&
                  new FileInfo(Path.Combine(folder, r2.FileName)).Length == 256, "両方とも完全なコピー");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    private static byte[] Sha256(string path) =>
        SHA256.HashData(File.ReadAllBytes(path));
}
