using System;
using System.Collections.Generic;

namespace PdfNote.Core;

/// <summary>
/// レンダリング済みページの LRU キャッシュ (生 BGRA バッファ)。
/// エントリ数と合計バイト数の両方で上限管理し、無制限にメモリを消費しない。
/// GPU テクスチャは管理しない (PdfCanvas 側で現在表示分のみ保持)。
/// </summary>
public sealed class PageBitmapCache
{
    /// <summary>キャッシュ再利用を許容する解像度の相対誤差。</summary>
    private const double ScaleTolerance = 0.15;

    private sealed class Entry
    {
        public required RenderedFrame Frame { get; init; }
    }

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _lru = new(); // 先頭 = 最近使用
    private readonly Dictionary<int, LinkedListNode<Entry>> _map = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _bytes;

    public PageBitmapCache(int maxEntries, long maxBytes)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
    }

    public (long Bytes, int Count) Stats
    {
        get
        {
            lock (_gate)
            {
                return (_bytes, _map.Count);
            }
        }
    }

    public void Add(RenderedFrame frame)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(frame.PageIndex, out var existing))
            {
                _lru.Remove(existing);
                _bytes -= existing.Value.Frame.Pixels.LongLength;
                _map.Remove(frame.PageIndex);
            }

            var node = new LinkedListNode<Entry>(new Entry { Frame = frame });
            _lru.AddFirst(node);
            _map[frame.PageIndex] = node;
            _bytes += frame.Pixels.LongLength;

            EvictOverBudgetLocked();
        }
    }

    /// <summary>解像度が要求に十分近いキャッシュを取得する (許容誤差 ±15%)。</summary>
    public bool TryGet(int pageIndex, double desiredPxPerPt, out RenderedFrame frame)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(pageIndex, out var node))
            {
                var candidate = node.Value.Frame;
                if (candidate.Pixels.LongLength > 0 && desiredPxPerPt > 0)
                {
                    var ratio = candidate.PxPerPt / desiredPxPerPt;
                    if (ratio >= 1.0 - ScaleTolerance && ratio <= 1.0 + ScaleTolerance)
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        frame = candidate;
                        return true;
                    }
                }
            }

            frame = null!;
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lru.Clear();
            _map.Clear();
            _bytes = 0;
        }
    }

    private void EvictOverBudgetLocked()
    {
        // 先頭 (追加したばかりのページ) は残し、LRU 末尾から削除する。
        while (_map.Count > 1 && (_map.Count > _maxEntries || _bytes > _maxBytes))
        {
            var last = _lru.Last;
            if (last is null)
            {
                break;
            }

            _lru.Remove(last);
            _map.Remove(last.Value.Frame.PageIndex);
            _bytes -= last.Value.Frame.Pixels.LongLength;
        }
    }
}
