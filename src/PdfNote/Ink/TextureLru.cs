using System;
using System.Collections.Generic;

namespace PdfNote.Ink;

/// <summary>
/// Ink Texture の LRU 管理ロジック (Win2D 非依存のためユニットテスト可能)。
/// ストロークデータ (NotebookPage.Ink) を正本とし、テクスチャは再生成可能なキャッシュ。
/// 破棄されたページは再表示時に EnsureTexture がストロークから再構築する。
/// </summary>
public sealed class TextureLru
{
    private readonly int _capacity;
    private readonly LinkedList<int> _order = new(); // 先頭 = 最近使用
    private readonly Dictionary<int, LinkedListNode<int>> _map = new();

    public TextureLru(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _map.Count;

    /// <summary>ページを使用済みとして記録する。</summary>
    public void MarkUsed(int pageIndex)
    {
        if (_map.TryGetValue(pageIndex, out var node))
        {
            _order.Remove(node);
            node.Value = pageIndex;
            _order.AddFirst(node);
        }
        else
        {
            _map[pageIndex] = _order.AddFirst(pageIndex);
        }
    }

    /// <summary>ページを管理対象から外す (破棄済み/RemovePage)。</summary>
    public void Remove(int pageIndex)
    {
        if (_map.Remove(pageIndex, out var node))
        {
            _order.Remove(node);
        }
    }

    /// <summary>すべての管理状態を破棄する。</summary>
    public void Clear()
    {
        _order.Clear();
        _map.Clear();
    }

    /// <summary>
    /// 上限超過分を LRU 側から列挙する。除外対象 (<paramref name="protectedPageIndex"/>,
    /// 負値で全排除禁止) は破棄しない。戻り値は破棄してよいページ番号。
    /// </summary>
    public IReadOnlyList<int> Evict(int protectedPageIndex)
    {
        List<int>? evicted = null;
        while (_map.Count > _capacity)
        {
            var node = FindLastUnprotected(protectedPageIndex);
            if (node is null)
            {
                break;
            }

            var key = node.Value;
            _order.Remove(node);
            _map.Remove(key);
            (evicted ??= new List<int>()).Add(key);
        }

        return (IReadOnlyList<int>?)evicted ?? Array.Empty<int>();
    }

    private LinkedListNode<int>? FindLastUnprotected(int protectedPageIndex)
    {
        for (var node = _order.Last; node is not null; node = node.Previous)
        {
            if (node.Value != protectedPageIndex)
            {
                return node;
            }
        }

        return null;
    }
}
