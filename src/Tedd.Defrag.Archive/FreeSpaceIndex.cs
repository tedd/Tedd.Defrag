using Tedd.Defrag.Core;

namespace Tedd.Defrag.Archive.V1;

/// <summary>Array-backed augmented treap. Address lookup and first-fit are expected O(log n).
/// Reservations reuse storage and allocate nothing once capacity is established.</summary>
public sealed class FreeSpaceIndex
{
    private struct Node { public long Start, Length, Max; public int Left, Right; public uint Priority; }
    private Node[] _nodes;
    private int _count, _root;
    private uint _random = 0xA341316C;
    public FreeSpaceIndex(IEnumerable<ClusterRange> ranges, int capacity = 4096)
    {
        _nodes = new Node[Math.Max(16, capacity)];
        foreach (var range in ranges) Add(range.Start, range.Length);
    }
    public long Largest => Max(_root);
    private long Max(int n) => n == 0 ? 0 : _nodes[n].Max;
    private void Update(int n) { if (n != 0) _nodes[n].Max = Math.Max(_nodes[n].Length, Math.Max(Max(_nodes[n].Left), Max(_nodes[n].Right))); }
    private uint Next() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return _random; }
    private void Add(long start, long length)
    {
        if (start < 0 || length <= 0 || start > long.MaxValue - length) throw new ArgumentOutOfRangeException(nameof(length));
        if (_count + 1 == _nodes.Length) Array.Resize(ref _nodes, checked(_nodes.Length * 2));
        int n = ++_count; _nodes[n] = new() { Start = start, Length = length, Max = length, Priority = Next() };
        _root = Insert(_root, n);
    }
    private int RotateRight(int n) { int l = _nodes[n].Left; _nodes[n].Left = _nodes[l].Right; _nodes[l].Right = n; Update(n); Update(l); return l; }
    private int RotateLeft(int n) { int r = _nodes[n].Right; _nodes[n].Right = _nodes[r].Left; _nodes[r].Left = n; Update(n); Update(r); return r; }
    private int Insert(int root, int n)
    {
        if (root == 0) return n;
        if (_nodes[n].Start < _nodes[root].Start) { _nodes[root].Left = Insert(_nodes[root].Left, n); if (_nodes[_nodes[root].Left].Priority < _nodes[root].Priority) root = RotateRight(root); }
        else { _nodes[root].Right = Insert(_nodes[root].Right, n); if (_nodes[_nodes[root].Right].Priority < _nodes[root].Priority) root = RotateLeft(root); }
        Update(root); return root;
    }
    private int Remove(int n, long start)
    {
        if (n == 0) return 0;
        if (start < _nodes[n].Start) _nodes[n].Left = Remove(_nodes[n].Left, start);
        else if (start > _nodes[n].Start) _nodes[n].Right = Remove(_nodes[n].Right, start);
        else
        {
            if (_nodes[n].Left == 0) return _nodes[n].Right;
            if (_nodes[n].Right == 0) return _nodes[n].Left;
            if (_nodes[_nodes[n].Left].Priority < _nodes[_nodes[n].Right].Priority) { n = RotateRight(n); _nodes[n].Right = Remove(_nodes[n].Right, start); }
            else { n = RotateLeft(n); _nodes[n].Left = Remove(_nodes[n].Left, start); }
        }
        Update(n); return n;
    }
    public bool Contains(long start, long length)
    {
        int n = _root, candidate = 0;
        while (n != 0) { if (_nodes[n].Start <= start) { candidate = n; n = _nodes[n].Right; } else n = _nodes[n].Left; }
        return candidate != 0 && length > 0 && start - _nodes[candidate].Start <= _nodes[candidate].Length - length;
    }
    public bool Reserve(long start, long length)
    {
        int n = _root, candidate = 0;
        while (n != 0) { if (_nodes[n].Start <= start) { candidate = n; n = _nodes[n].Right; } else n = _nodes[n].Left; }
        if (candidate == 0 || length <= 0) return false;
        var old = _nodes[candidate];
        if (start - old.Start > old.Length - length) return false;
        _root = Remove(_root, old.Start);
        if (old.Start < start) Add(old.Start, start - old.Start);
        long end = checked(start + length), oldEnd = old.Start + old.Length;
        if (end < oldEnd) Add(end, oldEnd - end);
        return true;
    }
    public long FindFirstFit(long length, long before = long.MaxValue, long after = 0) => Find(_root, length, before, after);
    private long Find(int n, long length, long before, long after)
    {
        if (n == 0 || length <= 0 || _nodes[n].Max < length) return -1;
        var node = _nodes[n];
        if (node.Start >= before) return Find(node.Left, length, before, after);
        if (node.Start + node.Length <= after) return Find(node.Right, length, before, after);
        long candidate = Find(node.Left, length, before, after);
        if (candidate >= 0) return candidate;
        long start = Math.Max(after, node.Start);
        if (start <= before - length && start <= node.Start + node.Length - length) return start;
        return Find(node.Right, length, before, after);
    }
}
