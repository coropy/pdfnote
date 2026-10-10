using PdfNote.Core;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ3: 重複統合・陳腐化識別の検証。</summary>
internal static class RenderServiceConcurrencyTests
{
    internal static async System.Threading.Tasks.Task RunAllAsync(Action<bool, string> check)
    {
        await DedupeIdenticalRequestsAsync(check);
        await StaleRequestIdentificationAsync(check);
    }

    internal static async System.Threading.Tasks.Task DedupeIdenticalRequestsAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_Dedupe");
        var root = RenderServiceTests.NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), pageCount: 1);
            var bundle = await service.OpenAsync(Guid.NewGuid(), pdf);

            var r1 = service.Render(bundle.ResourceId, 0, 2.0);
            var r2 = service.Render(bundle.ResourceId, 0, 2.0);
            var t1 = await r1.Task;
            var t2 = await r2.Task;
            check(ReferenceEquals(t1.Frame, t2.Frame), "重複要求は同一フレーム (統合)");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static async System.Threading.Tasks.Task StaleRequestIdentificationAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_StaleIdentification");
        var root = RenderServiceTests.NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), pageCount: 1);
            var bundle = await service.OpenAsync(Guid.NewGuid(), pdf);

            // 解像度を変えた新要求が旧要求を陳腐化させる。
            var old = service.Render(bundle.ResourceId, 0, 2.0);
            var current = service.Render(bundle.ResourceId, 0, 3.0);
            check(old.RequestId != current.RequestId, "RequestId は要求ごとに一意");

            try
            {
                var oldResult = await old.Task;
                // 旧処理が既に描画中で完了した場合でも、呼び出し側は RequestId 比較で破棄できる。
                check(oldResult.RequestId != current.RequestId, "完了済み旧結果は RequestId 不一致で識別できる");
            }
            catch (TaskCanceledException)
            {
                check(true, "陳腐化要求はキャンセルされる");
            }

            var currentResult = await current.Task;
            check(currentResult.RequestId == current.RequestId, "最新要求の結果は RequestId 一致");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }
}
