using System;
using System.Collections.Generic;

namespace PdfNote.View;

/// <summary>
/// LRU + 総バイト予算の純粋管理 (エントリは呼び出し側が所有する任意オブジェクト)。
/// PDF オブジェクト用 GPU テクスチャキャッシュの上限管理ロジック (テスト可能)。
/// InkRenderer / TextureLru とは独立した別キャッシュ。
/// </summary>
internal sealed class TextureCacheCore<TKey> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly Dictionary<TKey, (object Entry, long Bytes)> _map = new();
    private readonly LinkedList<TKey> _lru = new(); // 先頭 = 最近使用

    public TextureCacheCore(int capacity, long maxBytes)
    {
        _capacity = Math.Max(1, capacity);
        _maxBytes = Math.Max(1, maxBytes);
    }

    /// <summary>ヒット時は最近使用順を更新する (再追加・再アップロードは不要)。</summary>
    public bool TryGet(TKey key, out object entry)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var found))
            {
                var node = _lru.Find(key);
                if (node is not null)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }

                entry = found.Entry;
                return true;
            }

            entry = null!;
            return false;
        }
    }

    /// <summary>
    /// エントリを追加する。上限 (件数 / 総バイト) 超過分は LRU 側から退避し、
    /// 退避したキーを <paramref name="evicted"/> に返す (呼び出し側がリソースを破棄する)。
    /// 追加したエントリ自身は退避しない。
    /// </summary>
    public void Add(TKey key, object entry, long bytes, List<TKey>? evicted)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                evicted?.Add(key);
                _bytes -= existing.Bytes;
                _map.Remove(key);
                var oldNode = _lru.Find(key);
                if (oldNode is not null)
                {
                    _lru.Remove(oldNode);
                }
            }

            _map[key] = (entry, bytes);
            _bytes += bytes;
            _lru.AddFirst(key);

            while (_map.Count > 1 && (_map.Count > _capacity || _bytes > _maxBytes))
            {
                var last = _lru.Last;
                if (last is null || EqualityComparer<TKey>.Default.Equals(last.Value, key))
                {
                    break; // 追加したばかりのエントリは残す。
                }

                if (_map.Remove(last.Value, out var removed))
                {
                    _bytes -= removed.Bytes;
                    evicted?.Add(last.Value);
                }

                _lru.Remove(last);
            }
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_map.Remove(key, out var removed))
            {
                return false;
            }

            _bytes -= removed.Bytes;
            var node = _lru.Find(key);
            if (node is not null)
            {
                _lru.Remove(node);
            }

            return true;
        }
    }

    /// <summary>指定キーを取り除き、エントリを返す (存在しなければ false)。</summary>
    public bool TryRemove(TKey key, out object entry)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var removed))
            {
                _bytes -= removed.Bytes;
                var node = _lru.Find(key);
                if (node is not null)
                {
                    _lru.Remove(node);
                }

                entry = removed.Entry;
                return true;
            }

            entry = null!;
            return false;
        }
    }

    /// <summary>すべてのエントリを取り除く (呼び出し側がリソースを破棄する)。</summary>
    public IReadOnlyList<(TKey Key, object Entry)> Clear()
    {
        lock (_gate)
        {
            var all = new List<(TKey, object)>(_map.Count);
            foreach (var pair in _map)
            {
                all.Add((pair.Key, pair.Value.Entry));
            }

            _map.Clear();
            _lru.Clear();
            _bytes = 0;
            return all;
        }
    }

    public (int Count, long Bytes) Stats
    {
        get
        {
            lock (_gate)
            {
                return (_map.Count, _bytes);
            }
        }
    }

    private long _bytes;
}
