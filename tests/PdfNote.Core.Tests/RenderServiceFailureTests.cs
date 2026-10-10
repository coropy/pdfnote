using PdfNote.Core;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ3: ページ範囲・ファイル失敗・解像度検証。</summary>
internal static class RenderServiceFailureTests
{
    internal static async System.Threading.Tasks.Task RunAllAsync(Action<bool, string> check)
    {
        await RejectInvalidPageIndexAsync(check);
        await ReportFileFailuresAsync(check);
        RejectInvalidScale(check);
    }

    internal static async System.Threading.Tasks.Task RejectInvalidPageIndexAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_RejectInvalidPageIndex");
        var root = RenderServiceTests.NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), pageCount: 2);
            var bundle = await service.OpenAsync(Guid.NewGuid(), pdf);

            foreach (var pageIndex in new[] { -1, 2 })
            {
                var threw = false;
                try
                {
                    service.Render(bundle.ResourceId, pageIndex, 2.0);
                }
                catch (PdfResourceServiceException ex) when (ex.Error == PdfResourceServiceError.PageOutOfRange)
                {
                    threw = true;
                }

                check(threw, $"ページ番号 {pageIndex} を描画開始前に拒否");
            }

            var notOpen = false;
            try
            {
                service.Render(Guid.NewGuid(), 0, 2.0);
            }
            catch (PdfResourceServiceException ex) when (ex.Error == PdfResourceServiceError.NotOpen)
            {
                notOpen = true;
            }

            check(notOpen, "未開リソースは NotOpen");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static async System.Threading.Tasks.Task ReportFileFailuresAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_FileFailures");
        var root = RenderServiceTests.NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var missing = false;
            try
            {
                await service.OpenAsync(Guid.NewGuid(), Path.Combine(root, "missing.pdf"));
            }
            catch (PdfOpenException)
            {
                missing = true;
            }

            check(missing, "不存在ファイルは PdfOpenException");

            var brokenPath = Path.Combine(root, "broken.pdf");
            File.WriteAllBytes(brokenPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var broken = false;
            try
            {
                await service.OpenAsync(Guid.NewGuid(), brokenPath);
            }
            catch (PdfOpenException)
            {
                broken = true;
            }

            check(broken, "破損 PDF は PdfOpenException");

            // 失敗したオープンでスロットが残らない (続けて 4 個開ける)。
            for (var i = 0; i < 4; i++)
            {
                var ok = await service.OpenAsync(
                    Guid.NewGuid(), TestPdf.Write(Path.Combine(root, $"ok{i}.pdf"), 1));
                check(ok.PageCount == 1, $"失敗後もオープン可能 ({i + 1}/4)");
            }
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    internal static void RejectInvalidScale(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_RejectInvalidScale");
        using var service = new PdfResourceRenderService();
        foreach (var scale in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            var threw = false;
            try
            {
                service.Render(Guid.NewGuid(), 0, scale);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            check(threw, $"不正解像度 {scale} を拒否");
        }
    }
}
