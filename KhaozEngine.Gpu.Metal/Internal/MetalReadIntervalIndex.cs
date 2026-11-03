using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Reusable AVL interval index. Span pruning never replaces the exact strided-byte overlap test.</summary>
internal sealed class MetalReadIntervalIndex
{
    struct Node
    {
        internal MetalCpuRead Read;
        internal ulong End, MaxEnd;
        internal int Left, Right, Height;
    }

    // Index zero is the empty subtree. Removed nodes form a free list in Left, without retaining resources.
    Node[] _nodes = new Node[16];
    int _root, _used, _free;
    internal long ProbeStepsForTests { get; private set; }

    internal void Clear()
    {
        Array.Clear(_nodes, 0, _used + 1);
        _root = _used = _free = 0;
        ProbeStepsForTests = 0;
    }

    internal void Add(in MetalCpuRead read) => _root = Insert(_root, read);

    // This index contains only contiguous intervals. Every removed interval pays for its own coalescing
    // work, giving amortized logarithmic insertion even for a transfer spanning many earlier reads.
    internal void AddContiguous(in MetalCpuRead read)
    {
        ulong start = read.Offset, end = checked(start + read.Length);
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

    int Insert(int index, in MetalCpuRead read)
    {
        if (index == 0) return Allocate(read);
        ProbeStepsForTests++;
        int comparison = Compare(read, _nodes[index].Read);
        if (comparison == 0) return index;
        // Do not hold a node ref across recursion, which can grow the backing array.
        if (comparison < 0)
        {
            int child = Insert(_nodes[index].Left, read);
            _nodes[index].Left = child;
        }
        else
        {
            int child = Insert(_nodes[index].Right, read);
            _nodes[index].Right = child;
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
        ulong end = read.Offset + read.Length + (read.Stride == 0 ? 0 : (read.Count - 1) * read.Stride);
        _nodes[index] = new() { Read = read, End = end, MaxEnd = end, Height = 1 };
        return index;
    }

    int Remove(int index, MetalCpuRead read)
    {
        if (index == 0) return 0;
        ProbeStepsForTests++;
        int comparison = Compare(read, _nodes[index].Read);
        if (comparison < 0) _nodes[index].Left = Remove(_nodes[index].Left, read);
        else if (comparison > 0) _nodes[index].Right = Remove(_nodes[index].Right, read);
        else
        {
            if (_nodes[index].Left == 0 || _nodes[index].Right == 0)
            {
                int child = _nodes[index].Left == 0 ? _nodes[index].Right : _nodes[index].Left;
                _nodes[index] = default;
                _nodes[index].Left = _free;
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
        }
        return Balance(index);
    }

    void Refresh(int index)
    {
        ref var node = ref _nodes[index];
        node.Height = 1 + Math.Max(_nodes[node.Left].Height, _nodes[node.Right].Height);
        node.MaxEnd = Math.Max(node.End, Math.Max(_nodes[node.Left].MaxEnd, _nodes[node.Right].MaxEnd));
    }

    int Balance(int index)
    {
        Refresh(index);
        int left = _nodes[index].Left, right = _nodes[index].Right;
        if (_nodes[left].Height - _nodes[right].Height > 1)
        {
            if (_nodes[_nodes[left].Left].Height < _nodes[_nodes[left].Right].Height)
                _nodes[index].Left = RotateLeft(left);
            return RotateRight(index);
        }
        if (_nodes[right].Height - _nodes[left].Height > 1)
        {
            if (_nodes[_nodes[right].Right].Height < _nodes[_nodes[right].Left].Height)
                _nodes[index].Right = RotateRight(right);
            return RotateLeft(index);
        }
        return index;
    }

    int RotateLeft(int index)
    {
        ProbeStepsForTests++;
        int root = _nodes[index].Right;
        _nodes[index].Right = _nodes[root].Left;
        _nodes[root].Left = index;
        Refresh(index);
        Refresh(root);
        return root;
    }

    int RotateRight(int index)
    {
        ProbeStepsForTests++;
        int root = _nodes[index].Left;
        _nodes[index].Left = _nodes[root].Right;
        _nodes[root].Right = index;
        Refresh(index);
        Refresh(root);
        return root;
    }

    static int Compare(in MetalCpuRead left, in MetalCpuRead right)
    {
        int result = left.Offset.CompareTo(right.Offset);
        if (result == 0) result = left.Length.CompareTo(right.Length);
        if (result == 0) result = left.Stride.CompareTo(right.Stride);
        if (result == 0) result = left.Count.CompareTo(right.Count);
        return result;
    }
}
