using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfNote.Core;

namespace PdfNote.Session;

/// <summary>PDF オブジェクト レンダリング結果。<see cref="RequestId"/> で要求の新旧を判定する。</summary>
public sealed class PdfObjectRenderResult
{
    public long RequestId { get; init; }

    public Guid ResourceId { get; init; }

    public int PageIndex { get; init; }

    /// <summary>実際にレンダリングに使われた解像度 (クランプ後)。</summary>
    public double PxPerPt { get; init; }

    public RenderedFrame Frame { get; init; } = null!;

    /// <summary>CPU キャッシュ (PageBitmapCache) からの再利用か。</summary>
    public bool FromCache { get; init; }
}

/// <summary>
/// レンダリング要求。生成時に RequestId が確定する。
/// 呼び出し側は「オブジェクトごとに最新の RequestId」を保持し、完了時に一致確認することで
/// 古い結果 (キャンセル対応前の処理が後から完了したケース) の採用を防止する。
/// </summary>
public sealed class PdfObjectRenderRequest
{
    internal PdfObjectRenderRequest(
        long requestId, Guid resourceId, int pageIndex, double pxPerPt, Task<PdfObjectRenderResult> task)
    {
        RequestId = requestId;
        ResourceId = resourceId;
        PageIndex = pageIndex;
        RequestedPxPerPt = pxPerPt;
        Task = task;
    }

    public long RequestId { get; }

    public Guid ResourceId { get; }

    public int PageIndex { get; }

    public double RequestedPxPerPt { get; }

    /// <summary>成功 / キャンセル (陳腐化) / 失敗のいずれかで完了する。</summary>
    public Task<PdfObjectRenderResult> Task { get; }
}

/// <summary>サービス固有の失敗種別。</summary>
public enum PdfResourceServiceError
{
    /// <summary>リソースが開かれていない (開放中を含む)。</summary>
    NotOpen,

    /// <summary>ページ番号が PDF の実ページ数の範囲外。</summary>
    PageOutOfRange,

    /// <summary>同時オープン上限に達し、退避できるアイドル資源がない。</summary>
    CapacityExhausted,
}

/// <summary>PdfResourceRenderService の明示的な失敗 (成功扱いにしない)。</summary>
public sealed class PdfResourceServiceException : Exception
{
    public PdfResourceServiceException(PdfResourceServiceError error, string message)
        : base(message) => Error = error;

    public PdfResourceServiceError Error { get; }
}

/// <summary>
/// 開いている PDF リソース 1 束 = PdfDocument + PdfRenderQueue + PageBitmapCache。
/// 4 リソース上限は「この束 (開いているドキュメントとそのキュー)」を数える。
/// CPU ビットマップキャッシュは束ごとに保持され、束の退避と一緒に解放される (同一視しない)。
/// </summary>
public sealed class PdfResourceBundle
{
    internal readonly object Gate = new();

    internal PdfDocument? Document { get; private set; }

    internal PdfRenderQueue? Queue { get; private set; }

    internal PageBitmapCache? Cache { get; private set; }

    internal bool Opening { get; private set; } = true;

    internal bool Closing { get; set; }

    internal DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    internal Dictionary<int, PendingRender> Pending { get; } = new();

    /// <summary>寸法取得など軽量だが実行中の補助処理 (退避禁止の対象)。</summary>
    internal int AuxJobs { get; set; }

    public Guid ResourceId { get; }

    public string FilePath { get; }

    public PdfResourceBundle(Guid resourceId, string filePath)
    {
        ResourceId = resourceId;
        FilePath = filePath;
    }

    /// <summary>束が閉じられた (退避 / 破棄) か。再オープンはサービスの OpenAsync で行う。</summary>
    public bool IsClosed { get; internal set; }

    /// <summary>実ページ数 (PdfDocument が既に開いている場合のみ。開放中は 0)。</summary>
    public int PageCount => Document?.PageCount ?? 0;

    /// <summary>実行中のレンダリング / 補助処理があるか (退避禁止の対象)。</summary>
    public bool IsBusy
    {
        get
        {
            lock (Gate)
            {
                if (AuxJobs > 0)
                {
                    return true;
                }

                foreach (var pending in Pending.Values)
                {
                    if (!pending.Superseded)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    internal void BeginAuxJob()
    {
        lock (Gate)
        {
            AuxJobs++;
        }
    }

    internal void EndAuxJob()
    {
        lock (Gate)
        {
            AuxJobs--;
        }

        MaybeCloseWhenIdle();
    }

    /// <summary>終了処理中かつ実行中処理が尽きた場合に束を解放する。</summary>
    internal void MaybeCloseWhenIdle()
    {
        var shouldClose = false;
        lock (Gate)
        {
            shouldClose = Closing && !IsClosed && !IsBusyInternal;
        }

        if (shouldClose)
        {
            Dispose();
        }
    }

    private bool IsBusyInternal
    {
        get
        {
            if (AuxJobs > 0)
            {
                return true;
            }

            foreach (var pending in Pending.Values)
            {
                if (!pending.Superseded)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>ページ番号が実ページ数の範囲内か (開放中は常に false)。</summary>
    public bool IsValidPage(int pageIndex) =>
        !Opening && Document is not null && (uint)pageIndex < (uint)Document.PageCount;

    internal void Attach(PdfDocument document)
    {
        Document = document;
        Queue = new PdfRenderQueue(document);
        Cache = new PageBitmapCache(maxEntries: 6, maxBytes: 192L * 1024 * 1024);
        Opening = false;
        LastUsedUtc = DateTime.UtcNow;
    }

    internal void MarkUsed() => LastUsedUtc = DateTime.UtcNow;

    internal void Dispose()
    {
        if (IsClosed)
        {
            return; // 二重解放防止。
        }

        IsClosed = true;
        Queue?.Dispose();
        try
        {
            Document?.Dispose();
        }
        catch (Exception)
        {
            // 破棄失敗は無視 (既存 PdfSession と同じ規約)。
        }

        Cache?.Clear();
        lock (Gate)
        {
            Pending.Clear();
        }
    }
}

/// <summary>同一ページへの実行中 / 待機中レンダリング。</summary>
internal sealed class PendingRender
{
    public PendingRender(int pageIndex, double pxPerPt, PdfRenderWaiter waiter)
    {
        PageIndex = pageIndex;
        PxPerPt = pxPerPt;
        Waiters.Add(waiter);
    }

    public int PageIndex { get; }

    /// <summary>代表解像度 (新規要求の重複判定に使用)。</summary>
    public double PxPerPt { get; }

    public List<PdfRenderWaiter> Waiters { get; } = new();

    /// <summary>キューに投入された後のタスク (投入前は null)。</summary>
    public Task<RenderedFrame>? QueueTask { get; set; }

    /// <summary>より新しい要求に置き換えられた (結果は破棄される)。</summary>
    public bool Superseded { get; set; }
}

/// <summary>要求者ごとの完了通知 (RequestId ごとに結果の新旧を判定できる)。</summary>
internal sealed class PdfRenderWaiter
{
    public PdfRenderWaiter(long requestId, TaskCompletionSource<PdfObjectRenderResult> tcs)
    {
        RequestId = requestId;
        Completion = tcs;
    }

    public long RequestId { get; }

    public TaskCompletionSource<PdfObjectRenderResult> Completion { get; }
}
