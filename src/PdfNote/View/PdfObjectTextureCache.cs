using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace PdfNote.View;

/// <summary>
/// PDF オブジェクト専用の GPU テクスチャキャッシュ (InkRenderer / TextureLru とは独立)。
/// - LRU 容量 6 かつ総バイト予算 192MB (16Mpx×4B=64MB/枚のため、最大解像度では実効 3 枚)。
/// - キーに解像度を含むため、ヒット時は再レンダリング・再アップロードを行わない。
/// - CanvasBitmap の生成 / 破棄はすべて UI スレッド (CanvasControl の描画スレッド) で行うこと。
/// - CPU 側フレーム (PageBitmapCache) と GPU テクスチャは別物。デバイスロスト等で
///   GPU テクスチャが無効になった場合は Remove して再アップロード / 再レンダリングできる。
/// </summary>
public sealed class PdfObjectTextureCache
{
    public const int Capacity = 6;

    public static readonly long MaxTotalBytes = 192L * 1024 * 1024;

    private readonly TextureCacheCore<PdfObjectTextureKey> _core = new(Capacity, MaxTotalBytes);

    /// <summary>(リソース, PDFページ) → 現在キャッシュ済みのキー (任意解像度での再利用用)。</summary>
    private readonly Dictionary<(Guid ResourceId, int PdfPageIndex), PdfObjectTextureKey> _latest = new();

    /// <summary>任意解像度のテクスチャを取得する (ズーム中の旧テクスチャ表示用)。</summary>
    public bool TryGetAny(Guid resourceId, int pdfPageIndex, out CanvasBitmap bitmap, out PdfObjectTextureKey key)
    {
        lock (_latest)
        {
            if (_latest.TryGetValue((resourceId, pdfPageIndex), out var found) && _core.TryGet(found, out var entry))
            {
                bitmap = (CanvasBitmap)entry;
                key = found;
                return true;
            }
        }

        bitmap = null!;
        key = default;
        return false;
    }

    /// <summary>指定キーのテクスチャを取得する。</summary>
    public bool TryGet(PdfObjectTextureKey key, out CanvasBitmap bitmap)
    {
        if (_core.TryGet(key, out var entry))
        {
            bitmap = (CanvasBitmap)entry;
            return true;
        }

        bitmap = null!;
        return false;
    }

    /// <summary>
    /// テクスチャを追加する。退避されたエントリを返す (呼び出し側が UI スレッドで Dispose する)。
    /// 16Mpx 超過のエントリは受け入れない (RenderScale の制約に従う)。
    /// </summary>
    public List<(PdfObjectTextureKey Key, CanvasBitmap Bitmap)> Add(
        PdfObjectTextureKey key, CanvasBitmap bitmap, int pixelWidth, int pixelHeight)
    {
        if (PdfObjectMath.ExceedsTextureBudget(pixelWidth, pixelHeight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pixelWidth),
                $"テクスチャが 16Mpx 上限を超えています ({pixelWidth}x{pixelHeight})。");
        }

        var evicted = new List<(PdfObjectTextureKey, CanvasBitmap)>();
        var evictedKeys = new List<PdfObjectTextureKey>();
        _core.Add(key, bitmap, PdfObjectMath.EstimateFrameBytes(pixelWidth, pixelHeight), evictedKeys);
        lock (_latest)
        {
            _latest[(key.ResourceId, key.PdfPageIndex)] = key;
        }

        foreach (var evictedKey in evictedKeys)
        {
            if (_core.TryRemove(evictedKey, out var removed))
            {
                evicted.Add((evictedKey, (CanvasBitmap)removed));
            }

            lock (_latest)
            {
                if (_latest.TryGetValue((evictedKey.ResourceId, evictedKey.PdfPageIndex), out var mapped) &&
                    mapped.Equals(evictedKey))
                {
                    _latest.Remove((evictedKey.ResourceId, evictedKey.PdfPageIndex));
                }
            }
        }

        return evicted;
    }

    /// <summary>テクスチャを取り除く (デバイスロスト等)。取り除いたビットマップを返す (呼び出し側で Dispose)。</summary>
    public bool Remove(PdfObjectTextureKey key, out CanvasBitmap removed)
    {
        if (_core.TryRemove(key, out var entry))
        {
            removed = (CanvasBitmap)entry;
            lock (_latest)
            {
                if (_latest.TryGetValue((key.ResourceId, key.PdfPageIndex), out var mapped) && mapped.Equals(key))
                {
                    _latest.Remove((key.ResourceId, key.PdfPageIndex));
                }
            }

            return true;
        }

        removed = null!;
        return false;
    }

    /// <summary>すべて破棄する (ノート切替など)。返されたビットマップを呼び出し側が Dispose する。</summary>
    public List<(PdfObjectTextureKey Key, CanvasBitmap Bitmap)> Clear()
    {
        var all = new List<(PdfObjectTextureKey, CanvasBitmap)>();
        foreach (var pair in _core.Clear())
        {
            all.Add((pair.Key, (CanvasBitmap)pair.Entry));
        }

        lock (_latest)
        {
            _latest.Clear();
        }

        return all;
    }

    public (int Count, long EstimatedBytes) Stats
    {
        get
        {
            var (count, bytes) = _core.Stats;
            return (count, bytes);
        }
    }
}
