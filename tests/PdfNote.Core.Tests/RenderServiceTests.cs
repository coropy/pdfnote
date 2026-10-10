using PdfNote.Core;
using PdfNote.Session;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ3: PdfResourceRenderService の検証 (実 PDF を使用)。</summary>
internal static class RenderServiceTests
{
    /// <summary>全テストを順次実行 (非同期)。Program.Main から同期呼び出しされる。</summary>
    internal static async System.Threading.Tasks.Task RunAllAsync(Action<bool, string> check)
    {
        await OpenAndPageSizeAsync(check);
        await RenderValidPageAsync(check);
        await CacheReuseAsync(check);
    }

    internal static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pdfnote-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<PdfResourceBundle> OpenOneAsync(PdfResourceRenderService service, Guid id, string pdfPath) =>
        await service.OpenAsync(id, pdfPath);

    private static async Task OpenAndPageSizeAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_OpenAndPageSize");
        var root = NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), pageCount: 2, widthPt: 200, heightPt: 100);
            var bundle = await OpenOneAsync(service, Guid.NewGuid(), pdf);
            check(bundle.PageCount == 2, "実ページ数の取得");
            check(service.TryGetPageCount(Guid.NewGuid()) is null, "未開リソースのページ数は null");

            var (w, h) = await service.GetPageSizePtAsync(bundle.ResourceId, 1);
            check(Math.Abs(w - 200) < 0.01 && Math.Abs(h - 100) < 0.01, "ページ寸法 (pt)");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    private static async Task RenderValidPageAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_RenderValidPage");
        var root = NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), pageCount: 1);
            var bundle = await OpenOneAsync(service, Guid.NewGuid(), pdf);

            var request = service.Render(bundle.ResourceId, 0, 2.0);
            var result = await request.Task;
            check(result.Frame.PixelWidth == 400 && result.Frame.PixelHeight == 200, "2px/pt での出力サイズ");
            check(Math.Abs(result.PxPerPt - 2.0) < 0.001, "実効解像度");
            check(result.Frame.Pixels.Length == 400L * 200 * 4, "BGRA バッファサイズ");
            check(!result.FromCache, "初回はキャッシュではない");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }

    private static async Task CacheReuseAsync(Action<bool, string> check)
    {
        Console.WriteLine("RenderService_CacheReuse");
        var root = NewRoot();
        using var service = new PdfResourceRenderService();
        try
        {
            var pdf = TestPdf.Write(Path.Combine(root, "a.pdf"), pageCount: 1);
            var bundle = await OpenOneAsync(service, Guid.NewGuid(), pdf);

            var first = await service.Render(bundle.ResourceId, 0, 2.0).Task;
            var second = await service.Render(bundle.ResourceId, 0, 2.0).Task;
            check(second.FromCache, "同一条件の再要求はキャッシュから");
            check(ReferenceEquals(first.Frame, second.Frame), "同一フレーム (再レンダリングなし)");
        }
        finally
        {
            NotebookLibraryTests.TryDeleteDir(root);
        }
    }
}
