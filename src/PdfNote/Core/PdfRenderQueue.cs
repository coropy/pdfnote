using System;
using System.Threading;
using System.Threading.Tasks;

namespace PdfNote.Core;

/// <summary>
/// PDF ページのレンダリングを直列に行う専用バックグラウンドスレッド (latest-wins 方式)。
/// 新しい要求が到着した時点で未処理の旧要求はキャンセル (結果破棄) される。
/// 実行中のレンダリング自体は中断できないため、完了後に結果を破棄する。
/// </summary>
public sealed class PdfRenderQueue : IDisposable
{
    private sealed class RenderRequest
    {
        public required int PageIndex { get; init; }
        public required double PxPerPt { get; init; }
        public required TaskCompletionSource<RenderedFrame> Completion { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    private readonly PdfDocument _doc;
    private readonly Thread _thread;
    private readonly SemaphoreSlim _signal = new(initialCount: 0, maxCount: int.MaxValue);
    private RenderRequest? _pending;
    private volatile bool _disposed;

    public PdfRenderQueue(PdfDocument doc)
    {
        _doc = doc;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PdfRenderQueue",
            // UI (Composition) より低優先度にして N100 でも UI を優先させる。
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>レンダリング要求。新しい要求で置き換えられた場合、返される Task はキャンセルされる。</summary>
    public Task<RenderedFrame> RenderAsync(int pageIndex, double pxPerPt)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfRenderQueue));
        }

        var request = new RenderRequest
        {
            PageIndex = pageIndex,
            PxPerPt = pxPerPt,
            Completion = new TaskCompletionSource<RenderedFrame>(TaskCreationOptions.RunContinuationsAsynchronously),
            Cancellation = new CancellationTokenSource(),
        };

        var previous = Interlocked.Exchange(ref _pending, request);
        if (previous is not null)
        {
            // 旧要求は破棄 (latest-wins)。
            previous.Completion.TrySetCanceled();
            previous.Cancellation.Dispose();
        }

        _signal.Release();
        return request.Completion.Task;
    }

    private void Run()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_disposed)
            {
                return;
            }

            var request = Interlocked.Exchange(ref _pending, null);
            if (request is null || request.Cancellation.IsCancellationRequested)
            {
                request?.Cancellation.Dispose();
                continue;
            }

            try
            {
                var frame = _doc.RenderPage(request.PageIndex, request.PxPerPt, request.Cancellation.Token);
                if (request.Cancellation.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled();
                }
                else
                {
                    request.Completion.TrySetResult(frame);
                }
            }
            catch (OperationCanceledException)
            {
                request.Completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
            finally
            {
                request.Cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var pending = Interlocked.Exchange(ref _pending, null);
        pending?.Completion.TrySetCanceled();

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
