using PdfNote.Core;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ3: 上限 / 退避 / 終了の検証。</summary>
internal static class RenderServiceLifecycleTests
{
    internal static async System.Threading.Tasks.Task RunAllAsync(Action<bool, string> check)
    {
        await NoEvictWhileRenderingAsync(check);
        await CapacityLimitAndReopenAsync(check);
        await DisposeRejectsNewRequestsAsync(check);
    }

    internal static async System.Threading.Tasks.Task NoEvictWhileRenderingAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_NoEvictWhileRendering");
        var root = RenderServiceTests.NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            // 大きな解像度の描画で実行時間を確保する。
            var busyPdf = TestPdf.Write(Path.Combine(root, "busy.pdf"), 1, 200, 100);
            var busy = await service.OpenAsync(Guid.NewGuid(), busyPdf);
            var busyRequest = service.Render(busy.ResourceId, 0, 28.0); // 16Mpx 上限いっぱい。

            // 4 束上限いっぱいまで追加オープン (実行中束は退避されない)。
            for (var i = 0; i < 3; i++)
            {
                await service.OpenAsync(Guid.NewGuid(), TestPdf.Write(Path.Combine(root, $"f{i}.pdf"), 1));
            }

            // 5 束目: アイドル束は退避されるが busy 束は残る。
            await service.OpenAsync(Guid.NewGuid(), TestPdf.Write(Path.Combine(root, "f4.pdf"), 1));
            check(!busy.IsClosed, "レンダリング中の束は退避されない");
            check(busy.PageCount == 1, "実行中束は利用可能");

            var result = await busyRequest.Task;
            check(result.Frame.PixelWidth > 0, "実行中束の描画が完了");

            // 完了後は退避可能になる。
            await service.OpenAsync(Guid.NewGuid(), TestPdf.Write(Path.Combine(root, "f5.pdf"), 1));
            check(true, "完了後の退避で例外なし");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static async System.Threading.Tasks.Task CapacityLimitAndReopenAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_CapacityLimitAndReopen");
        var root = RenderServiceTests.NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdfPath = TestPdf.Write(Path.Combine(root, "a.pdf"), 1);
            var bundles = new List<PdfResourceBundle>();
            for (var i = 0; i < 4; i++)
            {
                bundles.Add(await service.OpenAsync(Guid.NewGuid(), pdfPath));
            }

            // すべてを実行中にする。
            var requests = bundles.Select(b => service.Render(b.ResourceId, 0, 28.0)).ToList();

            var exhausted = false;
            try
            {
                await service.OpenAsync(Guid.NewGuid(), pdfPath);
            }
            catch (PdfResourceServiceException ex) when (ex.Error == PdfResourceServiceError.CapacityExhausted)
            {
                exhausted = true;
            }

            check(exhausted, "退避不能な場合は CapacityExhausted を明示");

            await System.Threading.Tasks.Task.WhenAll(requests.Select(r => r.Task));
            var reopened = await service.OpenAsync(Guid.NewGuid(), pdfPath);
            check(reopened.PageCount == 1, "完了後は再オープン可能");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static async System.Threading.Tasks.Task DisposeRejectsNewRequestsAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_Dispose");
        var root = RenderServiceTests.NewRoot();
        var service = new PdfResourceRenderService();
        var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), 1);
        var bundle = await service.OpenAsync(Guid.NewGuid(), pdf);

        var pending = service.Render(bundle.ResourceId, 0, 2.0);
        service.Dispose();

        var pendingSettled = false;
        try
        {
            await pending.Task;
            pendingSettled = true; // 完了後に終了した場合は結果を得ることもある。
        }
        catch (TaskCanceledException)
        {
            pendingSettled = true;
        }

        check(pendingSettled, "実行中要求は完了またはキャンセルで収束");

        var renderRejected = false;
        try
        {
            service.Render(bundle.ResourceId, 0, 2.0);
        }
        catch (ObjectDisposedException)
        {
            renderRejected = true;
        }

        check(renderRejected, "終了後の新規要求は拒否");

        var openRejected = false;
        try
        {
            await service.OpenAsync(Guid.NewGuid(), pdf);
        }
        catch (ObjectDisposedException)
        {
            openRejected = true;
        }

        check(openRejected, "終了後のオープンは拒否");
        check(bundle.IsClosed, "アイドル束は終了時に解放");
        NotebookLibraryTests.TryDeleteDir(root);
    }
}
