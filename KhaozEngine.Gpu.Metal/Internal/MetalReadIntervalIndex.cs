using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Reusable AVL interval index. Span pruning never replaces the exact strided-byte overlap test.</summary>
internal sealed class MetalReadIntervalIndex(bool contiguous = false)
{
    struct Node
    {
        internal MetalCpuRead Read;
        internal ulong End, MaxEnd;
        internal int Left, Right, Height;
    }

    // Index zero is the empty subtree. Removed nodes form a free list in Left, without retaining resources.
    Node[] _nodes = new Node[16];
    int _root, _used, _free, _rightmost;
    internal long ProbeStepsForTests { get; private set; }
    internal long NodeWritesForTests { get; private set; }
    internal long InsertsForTests { get; private set; }
    internal long ExtendsForTests { get; private set; }
    internal long RotationsForTests { get; private set; }

    internal void ReserveLike(MetalReadIntervalIndex other)
    {
        if (_nodes.Length < other._nodes.Length) Array.Resize(ref _nodes, other._nodes.Length);
    }

    internal void Clear()
    {
        Array.Clear(_nodes, 0, _used + 1);
        _root = _used = _free = _rightmost = 0;
        ProbeStepsForTests = 0;
        NodeWritesForTests = 0;
        RotationsForTests = InsertsForTests = ExtendsForTests = 0;
    }

    internal void Add(in MetalCpuRead read)
    {
        int tail = 0;
        Add(read, ref tail);
    }

    internal void Add(in MetalCpuRead read, ref int tail)
    {
        if (read.Stride != 0 && HasUnwrappedEnd(read) && tail != 0)
        {
            ProbeStepsForTests++;
            var previous = _nodes[tail].Read;
            // Matching phase joins element sequences, never their bounding spans. A skipped element stays a gap.
            if (HasUnwrappedEnd(previous) && read.Length == previous.Length && read.Stride == previous.Stride
                && read.Offset >= previous.Offset && (read.Offset - previous.Offset) % read.Stride == 0
                && (read.Offset - previous.Offset) / read.Stride <= previous.Count)
            {
                ulong count = (read.Offset - previous.Offset) / read.Stride + read.Count;
                if (count <= previous.Count) return;
                ExtendsForTests++;
                Extend(_root, tail, previous with { Count = count });
                return;
            }
        }
        InsertsForTests++;
        _root = Insert(_root, read, out _, out tail);
    }

    // Only the end changes. Tree shape and key stay fixed, so refresh bounds without balancing.
    void Extend(int index, int tail, in MetalCpuRead read)
    {
        ProbeStepsForTests++;
        if (index == tail)
        {
            _nodes[index].Read = read;
            _nodes[index].End = EndOf(read);
            NodeWritesForTests++;
        }
        else Extend(Compare(read, _nodes[index].Read) < 0 ? _nodes[index].Left : _nodes[index].Right, tail, read);
        Refresh(index);
    }

    // This index contains only contiguous intervals. Every removed interval pays for its own coalescing
    // work, giving amortized logarithmic insertion even for a transfer spanning many earlier reads.
    internal void AddContiguous(in MetalCpuRead read)
    {
        ulong start = read.Offset, end = checked(start + read.Length);
        // Uniform-ring slices usually append. Their known right edge avoids two redundant bound searches.
        if (_rightmost != 0)
        {
            ProbeStepsForTests++;
            ref readonly var right = ref _nodes[_rightmost];
            if (start >= right.Read.Offset)
            {
                if (end <= right.End) return;
                if (start <= right.End) { Add(new(read.Resource, right.Read.Offset, end - right.Read.Offset)); return; }
                Add(read);
                return;
            }
        }
        int previous = Bound(start, floor: true);
        if (previous != 0 && _nodes[previous].End >= start)
        {
            if (_nodes[previous].End >= end) return;
            start = _nodes[previous].Read.Offset;
            end = Math.Max(end, _nodes[previous].End);
            _root = Remove(_root, _nodes[previous].Read);
        }
        int next = Bound(start, floor: false);
        while (next != 0 && _nodes[next].Read.Offset <= end)
        {
            end = Math.Max(end, _nodes[next].End);
            _root = Remove(_root, _nodes[next].Read);
            next = Bound(start, floor: false);
        }
        if (_rightmost == 0 && _root != 0) _rightmost = Bound(ulong.MaxValue, floor: true);
        Add(new(read.Resource, start, end - start));
    }

    internal bool Overlaps(in MetalCpuRead access)
        => access.Length != 0 && Overlaps(_root, access);

    bool Overlaps(int index, in MetalCpuRead access)
    {
        if (index == 0) return false;
        ProbeStepsForTests++;
        ref readonly var node = ref _nodes[index];
        if (node.MaxEnd <= access.Offset) return false;
        if (Overlaps(node.Left, access)) return true;
        // All remaining nodes start at or beyond this node. Half-open boundaries leave gaps writable.
        if (node.Read.Offset >= access.Offset + access.Length) return false;
        if (node.End > access.Offset && node.Read.Overlaps(access)) return true;
        return Overlaps(node.Right, access);
    }

    int Bound(ulong offset, bool floor)
    {
        int index = _root, result = 0;
        while (index != 0)
        {
            ProbeStepsForTests++;
            ulong start = _nodes[index].Read.Offset;
            if (start == offset) return index;
            if (start < offset)
            {
                if (floor) result = index;
                index = _nodes[index].Right;
            }
            else
            {
                if (!floor) result = index;
                index = _nodes[index].Left;
            }
        }
        return result;
    }

    int Insert(int index, in MetalCpuRead read, out bool changed, out int inserted)
    {
        if (index == 0) { changed = true; return inserted = Allocate(read); }
        ProbeStepsForTests++;
        int comparison = Compare(read, _nodes[index].Read);
        if (comparison == 0)
        {
            inserted = index;
            var previous = _nodes[index].Read;
            changed = read.Stride == 0 ? read.Length > previous.Length : read.Count > previous.Count;
            if (!changed) return index;
            _nodes[index].Read = read;
            _nodes[index].End = EndOf(read);
            NodeWritesForTests++;
            Refresh(index);
            return index;
        }
        // Do not hold a node ref across recursion, which can grow the backing array.
        if (comparison < 0)
        {
            int child = Insert(_nodes[index].Left, read, out changed, out inserted);
            if (!changed) return index;
            if (_nodes[index].Left != child) { _nodes[index].Left = child; NodeWritesForTests++; }
        }
        else
        {
            int child = Insert(_nodes[index].Right, read, out changed, out inserted);
            if (!changed) return index;
            if (_nodes[index].Right != child) { _nodes[index].Right = child; NodeWritesForTests++; }
        }
        return Balance(index);
    }

    int Allocate(in MetalCpuRead read)
    {
        int index;
        if (_free != 0)
        {
            index = _free;
            _free = _nodes[index].Left;
        }
        else
        {
            index = ++_used;
            if (index == _nodes.Length) Array.Resize(ref _nodes, checked(_nodes.Length * 2));
        }
        ulong end = EndOf(read);
        _nodes[index] = new() { Read = read, End = end, MaxEnd = end, Height = 1 };
        NodeWritesForTests++;
        if (_rightmost == 0 || read.Offset > _nodes[_rightmost].Read.Offset) _rightmost = index;
        return index;
    }

    int Remove(int index, MetalCpuRead read)
    {
        if (index == 0) return 0;
        ProbeStepsForTests++;
        int comparison = Compare(read, _nodes[index].Read);
        if (comparison < 0) { _nodes[index].Left = Remove(_nodes[index].Left, read); NodeWritesForTests++; }
        else if (comparison > 0) { _nodes[index].Right = Remove(_nodes[index].Right, read); NodeWritesForTests++; }
        else
        {
            if (_nodes[index].Left == 0 || _nodes[index].Right == 0)
            {
                int child = _nodes[index].Left == 0 ? _nodes[index].Right : _nodes[index].Left;
                if (index == _rightmost) _rightmost = 0;
                _nodes[index] = default;
                _nodes[index].Left = _free;
                NodeWritesForTests++;
                _free = index;
                return child;
            }
            int successor = _nodes[index].Right;
            while (_nodes[successor].Left != 0)
            {
                ProbeStepsForTests++;
                successor = _nodes[successor].Left;
            }
            _nodes[index].Read = _nodes[successor].Read;
            _nodes[index].End = _nodes[successor].End;
            _nodes[index].Right = Remove(_nodes[index].Right, _nodes[successor].Read);
            NodeWritesForTests++;
        }
        return Balance(index);
    }

    void Refresh(int index)
    {
        ref var node = ref _nodes[index];
        int height = 1 + Math.Max(_nodes[node.Left].Height, _nodes[node.Right].Height);
        ulong maxEnd = Math.Max(node.End, Math.Max(_nodes[node.Left].MaxEnd, _nodes[node.Right].MaxEnd));
        if (node.Height == height && node.MaxEnd == maxEnd) return;
        node.Height = height;
        node.MaxEnd = maxEnd;
        NodeWritesForTests++;
    }

    int Balance(int index)
    {
        Refresh(index);
        int left = _nodes[index].Left, right = _nodes[index].Right;
        if (_nodes[left].Height - _nodes[right].Height > 1)
        {
            if (_nodes[_nodes[left].Left].Height < _nodes[_nodes[left].Right].Height)
            {
                _nodes[index].Left = RotateLeft(left);
                NodeWritesForTests++;
            }
            return RotateRight(index);
        }
        if (_nodes[right].Height - _nodes[left].Height > 1)
        {
            if (_nodes[_nodes[right].Right].Height < _nodes[_nodes[right].Left].Height)
            {
                _nodes[index].Right = RotateRight(right);
                NodeWritesForTests++;
            }
            return RotateLeft(index);
        }
        return index;
    }

    int RotateLeft(int index)
    {
        RotationsForTests++;
        ProbeStepsForTests++;
        int root = _nodes[index].Right;
        _nodes[index].Right = _nodes[root].Left;
        _nodes[root].Left = index;
        NodeWritesForTests += 2;
        Refresh(index);
        Refresh(root);
        return root;
    }

    int RotateRight(int index)
    {
        RotationsForTests++;
        ProbeStepsForTests++;
        int root = _nodes[index].Left;
        _nodes[index].Left = _nodes[root].Right;
        _nodes[root].Right = index;
        NodeWritesForTests += 2;
        Refresh(index);
        Refresh(root);
        return root;
    }

    int Compare(in MetalCpuRead left, in MetalCpuRead right)
    {
        int result = left.Offset.CompareTo(right.Offset);
        if (contiguous) return result;
        if (result == 0) result = left.Length.CompareTo(right.Length);
        if (result == 0) result = left.Stride.CompareTo(right.Stride);
        // Only unwrapped sequences share a maximal count. Keep the old full key for overflowing spans,
        // whose wrapped end cannot stand in for an earlier sequence's protection.
        if (result == 0 && (!HasUnwrappedEnd(left) || !HasUnwrappedEnd(right)))
            result = left.Count.CompareTo(right.Count);
        return result;
    }

    static ulong EndOf(in MetalCpuRead read)
        => read.Offset + read.Length + (read.Stride == 0 ? 0 : (read.Count - 1) * read.Stride);

    static bool HasUnwrappedEnd(in MetalCpuRead read)
        => read.Length <= ulong.MaxValue - read.Offset && (read.Stride == 0
            || (read.Count != 0 && read.Count - 1 <= (ulong.MaxValue - read.Offset - read.Length) / read.Stride));
}
