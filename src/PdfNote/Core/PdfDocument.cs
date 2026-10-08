using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;

namespace PdfNote.Core;

/// <summary>PDF を開けなかった場合の例外。</summary>
public sealed class PdfOpenException : Exception
{
    public PdfOpenException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// PDFium (Docnet) の薄いラッパー。
/// レンダリングは呼び出し側スレッド (PdfRenderQueue) で行う。
/// Docnet は内部のグローバルロック (DocLib.Lock) で全 PDFium 呼び出しを直列化するためスレッドセーフ。
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly double[] _pageWidthsPt;
    private readonly double[] _pageHeightsPt;
    private volatile bool _disposed;

    public string FilePath { get; }

    /// <summary>ページ数。</summary>
    public int PageCount => _pageWidthsPt.Length;

    private PdfDocument(string filePath, double[] widthsPt, double[] heightsPt)
    {
        FilePath = filePath;
        _pageWidthsPt = widthsPt;
        _pageHeightsPt = heightsPt;
    }

    /// <summary>
    /// PDF を開き、全ページの自然寸法 (pt, 1pt = 1px) を取得する。
    /// 暗号化/破損/読み取り不能な場合は <see cref="PdfOpenException"/> を投げる。
    /// </summary>
    public static PdfDocument Open(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new PdfOpenException($"ファイルが見つかりません:\n{filePath}");
        }

        try
        {
            using var reader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1.0));

            var count = reader.GetPageCount();
            if (count <= 0)
            {
                throw new PdfOpenException("ページが含まれていないPDFです。");
            }

            var widths = new double[count];
            var heights = new double[count];
            for (var i = 0; i < count; i++)
            {
                using var page = reader.GetPageReader(i);
                widths[i] = page.GetPageWidth();
                heights[i] = page.GetPageHeight();
            }

            return new PdfDocument(filePath, widths, heights);
        }
        catch (DocnetException ex)
        {
            throw new PdfOpenException(
                "PDFを開けませんでした。\nパスワードで保護されているか、破損している可能性があります。", ex);
        }
        catch (PdfOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfOpenException("PDFを開けませんでした。", ex);
        }
    }

    /// <summary>ページの自然寸法をポイント単位で返す。</summary>
    public (double WidthPt, double HeightPt) GetPageSizePt(int pageIndex)
    {
        ThrowIfDisposed();
        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return (_pageWidthsPt[pageIndex], _pageHeightsPt[pageIndex]);
    }

    /// <summary>
    /// 指定ページを指定解像度 (px/pt) でラスタライズする。
    /// 呼び出しは PdfRenderQueue 専用スレッドから行うこと (直列実行)。
    /// </summary>
    public RenderedFrame RenderPage(int pageIndex, double pxPerPt, CancellationToken ct)
    {
        ThrowIfDisposed();
        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ct.ThrowIfCancellationRequested();

        using var reader = DocLib.Instance.GetDocReader(FilePath, new PageDimensions(pxPerPt));
        using var page = reader.GetPageReader(pageIndex);

        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        if (width <= 0 || height <= 0)
        {
            throw new PdfOpenException($"ページ {pageIndex + 1} の寸法を取得できませんでした。");
        }

        var stopwatch = Stopwatch.StartNew();
        var pixels = page.GetImage();
        stopwatch.Stop();

        ct.ThrowIfCancellationRequested();

        return new RenderedFrame(pageIndex, width, height, pixels, pxPerPt, stopwatch.Elapsed.TotalMilliseconds);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfDocument));
        }
    }

    public void Dispose()
    {
        // DocLib シングルトンはアプリ寿命まで保持するため、ここでは状態フラグのみ更新する。
        _disposed = true;
    }
}
