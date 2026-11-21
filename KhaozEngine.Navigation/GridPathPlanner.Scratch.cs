using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Collision;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>The scratch no query currently holds, or null while one is out (or before the first query).</summary>
    SearchScratch? _idleScratch;

    /// <summary>Takes the planner's idle scratch, allocating it on first use. A query that overlaps another
    /// (a second thread, or a region predicate that calls back into this planner) finds none idle and gets a
    /// fresh one for its own duration, so concurrent queries never share working memory.</summary>
    SearchScratch RentScratch()
        => Interlocked.Exchange(ref _idleScratch, null) ?? new SearchScratch(_totalNodes, _space.Layers.Count);

    void ReturnScratch(SearchScratch scratch) => Volatile.Write(ref _idleScratch, scratch);

    /// <summary>Test seam: moves the idle scratch's generation counter forward only, so a fact can drive the
    /// stamp wraparound without running two billion queries. A lower value would read live stamps as
    /// visited.</summary>
    internal void SeedScratchGeneration(int generation)
    {
        SearchScratch scratch = RentScratch();
        scratch.SeedGeneration(generation);
        ReturnScratch(scratch);
    }

    /// <summary>
    /// One query's working memory, reused across queries so a query touches only the nodes it visits.
    /// The per-node arrays are generation stamped: <c>stamp == generation</c> marks a node relaxed by this
    /// query (its g-score and parent are valid), <c>stamp == generation + 1</c> marks it closed, and any
    /// smaller stamp reads as untouched (infinite g-score, no parent). Each search advances the generation
    /// by two, so nothing is cleared between queries. When the counter would overflow, the stamps are
    /// cleared once and counting restarts.
    /// </summary>
    internal sealed class SearchScratch
    {
        readonly double[] _gScore;
        readonly int[] _cameFrom;
        readonly NavLinkKind?[] _incoming;
        readonly int[] _stamp;
        int _generation;

        readonly MemberBox[] _memberBoxes;
        readonly Array _memberLayers;
        bool[] _memberFlags;
        (int Layer, int Cx, int Cz)[] _cells;

        NavGrid? _rayGrid;
        float _rayRadius;
        readonly Func<int, int, bool> _rayBlocks;

        public SearchScratch(int totalNodes, int layerCount)
            : this(totalNodes, layerCount, memberCapacity: 0, frontierCapacity: 0, chainCapacity: 0)
        {
        }

        internal SearchScratch(int totalNodes, int layerCount, int memberCapacity, int frontierCapacity, int chainCapacity)
        {
            _gScore = new double[totalNodes];
            _cameFrom = new int[totalNodes];
            _incoming = new NavLinkKind?[totalNodes];
            _stamp = new int[totalNodes];
            _memberBoxes = new MemberBox[layerCount];
            _memberLayers = frontierCapacity == 0 ? new bool[layerCount] : new int[layerCount];
            _memberFlags = memberCapacity == 0 ? Array.Empty<bool>() : new bool[memberCapacity];
            _cells = chainCapacity == 0 ? Array.Empty<(int, int, int)>() : new (int, int, int)[chainCapacity];
            Open = frontierCapacity == 0 ? new() : new(frontierCapacity);
            Chain = new(chainCapacity);
            Waypoints = new(chainCapacity);
            Members = new(memberCapacity);
            MemberNodeIds = new(memberCapacity);
            _rayBlocks = (x, z) => Blocks(_rayGrid!, _rayRadius, x, z);
        }

        /// <summary>The search's open set, cleared by <see cref="BeginSearch"/>.</summary>
        public PriorityQueue<int, (double F, int Seq)> Open { get; }

        /// <summary>Reconstruction's node chain, start first.</summary>
        public List<int> Chain { get; }

        /// <summary>Reconstruction's waypoints, copied into each returned path.</summary>
        public List<NavWaypoint> Waypoints { get; }

        /// <summary>A region query's member feet positions, in grid scan order.</summary>
        public List<Vector3> Members { get; }

        public List<int> MemberNodeIds { get; }

        public PlannerRegionBounds MemberBounds { get; private set; }

        public void AddMember(Vector3 feet, int x, int z, int nodeId)
        {
            Members.Add(feet);
            MemberNodeIds.Add(nodeId);
            MemberBounds = MemberBounds.Include(x, z);
        }

        public bool NeedsWrap => _generation >= int.MaxValue - 2;

        public int ClearStampBlock(int offset)
        {
            int count = Math.Min(256, _stamp.Length - offset);
            Array.Clear(_stamp, offset, count);
            int end = offset + count;
            // Keep a wrapping generation until the last block. A cancelled wrap starts over on reuse.
            if (end == _stamp.Length) _generation = 0;
            return end;
        }

        public void BeginSearch()
        {
            _generation += 2;
            Open.Clear();
        }

        public void SeedGeneration(int generation) => _generation = generation & ~1;

        public double GScore(int id) => _stamp[id] >= _generation ? _gScore[id] : double.PositiveInfinity;

        public bool IsClosed(int id) => _stamp[id] == _generation + 1;

        public int CameFrom(int id) => _cameFrom[id];

        public NavLinkKind? Incoming(int id) => _incoming[id];

        /// <summary>Records an improved route into an open node.</summary>
        public void Relax(int id, double gScore, int cameFrom, NavLinkKind? kind = null)
        {
            _gScore[id] = gScore;
            _cameFrom[id] = cameFrom;
            _incoming[id] = kind;
            _stamp[id] = _generation;
        }

        public void Close(int id) => _stamp[id] = _generation + 1;

        /// <summary>Reconstruction's decoded chain cells, at least <paramref name="count"/> long. Grows to
        /// the longest chain seen and is reused after that.</summary>
        public (int Layer, int Cx, int Cz)[] CellsFor(int count)
        {
            if (_cells.Length < count) _cells = new (int, int, int)[Math.Max(count, _cells.Length * 2)];
            return _cells;
        }

        /// <summary>Starts membership with no members. Each box and its candidate status are recorded before use.</summary>
        public void ResetMembers()
        {
            Members.Clear();
            MemberNodeIds.Clear();
            MemberBounds = PlannerRegionBounds.Empty;
        }

        /// <summary>Sets <paramref name="layer"/>'s candidate box and returns the flag offset after it.</summary>
        public int SetMemberBox(int layer, int x0, int z0, int width, int height, int offset)
        {
            _memberBoxes[layer] = new MemberBox(x0, z0, width, height, offset);
            return offset + width * height;
        }

        // Synchronous scratch keeps one flag per layer. Prewarmed scratch keeps the nonempty layers
        // packed so a sliced advance can move to the next candidate layer in constant work.
        internal void RecordCandidateLayer(int layer, int index, bool hasCandidates)
        {
            if (_memberLayers is int[] layers)
            {
                if (hasCandidates) layers[index] = layer;
            }
            else
            {
                ((bool[])_memberLayers)[layer] = hasCandidates;
            }
        }

        internal int NextCandidateLayer(int previous, int index)
        {
            if (_memberLayers is int[] layers) return layers[index];
            var candidates = (bool[])_memberLayers;
            do { previous++; } while (!candidates[previous]);
            return previous;
        }

        /// <summary>Ensures flags fit the candidate boxes. Only synchronous scratch can need to grow here.</summary>
        public void EnsureMemberFlags(int cells)
        {
            if (_memberFlags.Length < cells) _memberFlags = new bool[cells];
        }

        /// <summary>Records whether box cell (<paramref name="x"/>, <paramref name="z"/>) of
        /// <paramref name="layer"/> is a member. Every cell of a box is written each query.</summary>
        public void SetMember(int layer, int x, int z, bool member)
        {
            MemberBox box = _memberBoxes[layer];
            _memberFlags[box.Offset + (z - box.Z0) * box.Width + (x - box.X0)] = member;
        }

        public MemberBox CandidateBox(int layer) => _memberBoxes[layer];

        public bool IsMember(int layer, int x, int z)
        {
            MemberBox box = _memberBoxes[layer];
            int dx = x - box.X0;
            int dz = z - box.Z0;
            return (uint)dx < (uint)box.Width && (uint)dz < (uint)box.Height &&
                _memberFlags[box.Offset + dz * box.Width + dx];
        }

        /// <summary>
        /// True when a straight line from <paramref name="fromWorldXz"/> to <paramref name="toWorldXz"/>
        /// crosses no cell that <see cref="Blocks(NavGrid, float, int, int)"/> for
        /// <paramref name="agentRadius"/>, via <see cref="GridRay.IsClear"/>. GridRay walks axis-aligned
        /// local cells, so both endpoints are inverse-transformed through the grid's translation and yaw
        /// first and path smoothing tests the same cells as A*. The blocked predicate is one cached delegate
        /// reading this scratch's ray fields, so a test allocates nothing.
        /// </summary>
        public bool HasLineOfSight(NavGrid grid, Vector2 fromWorldXz, Vector2 toWorldXz, float agentRadius)
        {
            _rayGrid = grid;
            _rayRadius = agentRadius;
            return GridRay.IsClear(
                grid.WorldToLocal(fromWorldXz), grid.WorldToLocal(toWorldXz), grid.CellSize,
                _rayBlocks, includeEndpointCells: false);
        }

        /// <summary>One layer's region candidate cells: a <c>Width</c> by <c>Height</c> box at
        /// (<c>X0</c>, <c>Z0</c>), its flags starting at <c>Offset</c>. A zero width holds no cells.</summary>
        public readonly record struct MemberBox(int X0, int Z0, int Width, int Height, int Offset);
    }
}
