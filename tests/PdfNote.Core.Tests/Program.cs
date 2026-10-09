namespace PdfNote.Core.Tests;

/// <summary>
/// 段階2 (白紙ノート + 既存 Ink 統合) の純粋ロジック検証。
/// 実行: dotnet run --project tests/PdfNote.Core.Tests
/// 依存パッケージなし (オフライン実行可能)。
/// </summary>
internal static class Program
{
    private static int _failures;

    public static int Main()
    {
        NotebookSessionTests.Basics(Check);
        NotebookSessionTests.SharedInkPageSurvivesDocumentAttach(Check);
        NotebookSerializerTests.RoundTripPreservesPagesAndStrokes(Check);
        NotebookSerializerTests.SecondSaveCreatesBackup(Check);
        NotebookSerializerTests.DropsInvalidStrokesWithoutCrashing(Check);
        NotebookSerializerTests.RejectsUnknownVersion(Check);

        Console.WriteLine(_failures == 0
            ? "ALL TESTS PASSED"
            : $"TESTS FAILED: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    internal static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine($"  ok: {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    internal static void TryDelete(string path)
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
            // クリーンアップ失敗は無視。
        }
    }
}
