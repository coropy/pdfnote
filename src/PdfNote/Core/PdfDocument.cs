using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Readers;
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
    private readonly IDocReader _dimsReader;
    private readonly double[] _pageWidthsPt;
    private readonly double[] _pageHeightsPt;
    private readonly object _dimsGate = new();
    private volatile bool _disposed;

    public string FilePath { get; }

    /// <summary>ページ数。</summary>
    public int PageCount => _pageWidthsPt.Length;

    private PdfDocument(string filePath, IDocReader dimsReader, int pageCount)
    {
        FilePath = filePath;
        _dimsReader = dimsReader;
        _pageWidthsPt = new double[pageCount];
        _pageHeightsPt = new double[pageCount];
    }

    /// <summary>
    /// PDF を開く。ページ数とページ0の寸法取得のみ行う (軽量・大規模PDFでも高速)。
    /// 暗号化/破損/読み取り不能な場合は <see cref="PdfOpenException"/> を投げる。
    /// </summary>
    public static PdfDocument Open(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new PdfOpenException($"ファイルが見つかりません:\n{filePath}");
        }

        IDocReader? dimsReader = null;
        try
        {
            dimsReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1.0));

            var count = dimsReader.GetPageCount();
            if (count <= 0)
            {
                dimsReader.Dispose();
                throw new PdfOpenException("ページが含まれていないPDFです。");
            }

            var document = new PdfDocument(filePath, dimsReader, count);
            document.LoadPageSize(0);
            return document;
        }
        catch (DocnetException ex)
        {
            dimsReader?.Dispose();
            throw new PdfOpenException(
                "PDFを開けませんでした。\nパスワードで保護されているか、破損している可能性があります。", ex);
        }
        catch (PdfOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            dimsReader?.Dispose();
            throw new PdfOpenException("PDFを開けませんでした。", ex);
        }
    }

    /// <summary>ページの自然寸法をポイント単位で返す (遅延読み込み & キャッシュ)。</summary>
    public (double WidthPt, double HeightPt) GetPageSizePt(int pageIndex)
    {
        ThrowIfDisposed();
        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        lock (_dimsGate)
        {
            if (_pageWidthsPt[pageIndex] > 0)
            {
                return (_pageWidthsPt[pageIndex], _pageHeightsPt[pageIndex]);
            }
        }

        LoadPageSize(pageIndex);

        lock (_dimsGate)
        {
            return (_pageWidthsPt[pageIndex], _pageHeightsPt[pageIndex]);
        }
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

        // バッファ整合性の検証 (不整合はGPUアップロードでクラッシュするため事前に検出)。
        if (pixels.LongLength != (long)width * height * 4)
        {
            throw new PdfOpenException(
                $"ページ {pageIndex + 1} の描画バッファが不正です (期待 {width * height * 4} / 実際 {pixels.LongLength})。");
        }

        // 寸法キャッシュが未取得ならレンダ結果から逆算して埋める。
        lock (_dimsGate)
        {
            if (_pageWidthsPt[pageIndex] <= 0 && pxPerPt > 0)
            {
                _pageWidthsPt[pageIndex] = width / pxPerPt;
                _pageHeightsPt[pageIndex] = height / pxPerPt;
            }
        }

        return new RenderedFrame(pageIndex, width, height, pixels, pxPerPt, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>ページ寸法を読み込んでキャッシュする (失敗時は 0 のまま = 寸法不明)。</summary>
    private void LoadPageSize(int pageIndex)
    {
        lock (_dimsGate)
        {
            if (_pageWidthsPt[pageIndex] > 0)
            {
                return;
            }
        }

        try
        {
            using var page = _dimsReader.GetPageReader(pageIndex);
            var width = page.GetPageWidth();
            var height = page.GetPageHeight();

            lock (_dimsGate)
            {
                _pageWidthsPt[pageIndex] = Math.Max(0, width);
                _pageHeightsPt[pageIndex] = Math.Max(0, height);
            }
        }
        catch (Exception)
        {
            // 壊れたページは寸法不明 (0) のままにして、レンダリング時にエラー処理へ委ねる。
        }
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _dimsReader.Dispose();
        }
        catch (Exception)
        {
            // 破棄失敗は無視 (アプリ継続優先)。
        }
    }
}
