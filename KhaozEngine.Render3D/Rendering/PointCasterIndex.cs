using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The frame's rigid point-shadow casters, binned by where they stand, so a light tests the casters near it
    /// instead of every queued instance (issue #1110). A pure CPU structure with no device and no scene, filled once
    /// per point-shadow frame by <see cref="Scene3D"/> and asked once per static signature and once per row the pass
    /// draws.
    /// <para>
    /// THE ANSWER IS THE FULL WALK'S ANSWER. A query returns exactly the slots a walk over every added caster keeps,
    /// in ascending slot order, because the grid only decides which casters are TESTED. The test is
    /// <c>Scene3D.InstanceTouchesLight</c> on the stored sphere, the one definition of touching a light (design
    /// decision 8 of the point shadow design). The grid may hand the test a caster that misses, and it never
    /// withholds one that could touch.
    /// </para>
    /// <para>
    /// A UNIFORM GRID OF <see cref="CellSize"/> METRE CELLS, binned by sphere centre. A caster no wider than a cell
    /// can only touch a light whose sphere, grown by one cell, contains its centre, so a query visits the cells
    /// under that grown sphere's box. A caster wider than a cell (a region ground chunk, a cliff) goes to an
    /// oversize list that every query tests, so a few huge casters never force a coarse grid on the many small
    /// ones. Cells are packed keys in a sorted array rather than a dense volume, so a world kilometres across costs
    /// only the cells that hold something.
    /// </para>
    /// <para>
    /// ALLOCATION-FREE ONCE WARM. Every array only grows, so a frame with no more casters or slots than the largest
    /// frame before it allocates nothing, which <c>PointShadowAllocationTests</c> holds the frame method to.
    /// </para>
    /// </summary>
    internal sealed class PointCasterIndex
    {
        /// <summary>The grid's cell edge in metres. Fixed: a lamp radius is a few cells, so a query visits tens of
        /// columns, and almost every prop is well under a cell wide, so the oversize list stays short.</summary>
        internal const float CellSize = 8f;

        /// <summary>Added to a query's grown reach before it becomes a cell range. The touch test runs in float, so
        /// at the very edge of a caster's reach it can keep a centre a rounding error past the grown sphere. A
        /// quarter metre is far above that error for any world under a thousand kilometres across, and slack can
        /// only add candidates, which the exact test then rejects.</summary>
        internal const float QuerySlack = 0.25f;

        const float InverseCellSize = 1f / CellSize;

        // Cell coordinates are packed 21 bits per axis into one sortable key, x major and z minor, so one (x, y)
        // column's z range is one contiguous stretch of the sorted keys. A centre whose cell falls outside that
        // range (thousands of kilometres out) or that is not finite goes to the oversize list instead.
        const int CellBias = 1 << 20;
        const int MinCell = -CellBias;
        const int MaxCell = CellBias - 1;

        // Per SLOT, and only meaningful for the slots added this frame: the world sphere (centre in xyz, radius in
        // w) and the mesh run the slot belongs to. Indexed by slot so a candidate list sorted by slot reads them
        // directly, and a slot not added this frame is never a candidate, so a stale entry is never read.
        Vector4[] _spheres = Array.Empty<Vector4>();
        int[] _runs = Array.Empty<int>();

        // The binned casters, parallel arrays sorted together by cell key in Seal.
        long[] _cellKeys = Array.Empty<long>();
        int[] _cellSlots = Array.Empty<int>();
        int _binned;

        int[] _oversize = Array.Empty<int>();
        int _oversizeCount;

        // One query's candidates before the exact test. Sized in Seal to every caster added, so no query grows it.
        int[] _candidates = Array.Empty<int>();
        bool _sealed;

        /// <summary>How many casters were added this frame, binned and oversize together.</summary>
        internal int Count => _binned + _oversizeCount;

        /// <summary>How many of them were too wide for a cell, or unbinnable, and are tested by every query.</summary>
        internal int OversizeCount => _oversizeCount;

        /// <summary>How many exact touch tests the queries since the last <see cref="Reset"/> ran. A diagnostic for
        /// the count tests, which assert it follows the casters near each light rather than every caster.</summary>
        internal int TouchTests { get; private set; }

        /// <summary>Forget the last frame's casters and make room for slots below <paramref name="slotCount"/>.
        /// Grows the per-slot arrays only when this frame has more slots than any frame before it.</summary>
        internal void Reset(int slotCount)
        {
            _binned = 0;
            _oversizeCount = 0;
            _sealed = false;
            TouchTests = 0;
            if (_spheres.Length >= slotCount) return;
            int capacity = GrownCapacity(_spheres.Length, slotCount);
            _spheres = new Vector4[capacity];
            _runs = new int[capacity];
        }

        /// <summary>Add one caster: its instance slot, the mesh run it belongs to and its world bounding sphere.
        /// Each slot is added at most once per frame. Call <see cref="Seal"/> after the last one.</summary>
        internal void Add(int slot, int run, Vector3 centre, float radius)
        {
            _spheres[slot] = new Vector4(centre, radius);
            _runs[slot] = run;
            // Written as a negation so a NaN radius, which fails every comparison, goes to the oversize list too.
            if (!(radius <= CellSize) || !TryCellOf(centre, out long key))
            {
                EnsureCapacity(ref _oversize, _oversizeCount + 1);
                _oversize[_oversizeCount++] = slot;
                return;
            }
            EnsureCapacity(ref _cellKeys, _binned + 1);
            EnsureCapacity(ref _cellSlots, _binned + 1);
            _cellKeys[_binned] = key;
            _cellSlots[_binned++] = slot;
        }

        /// <summary>Finish the frame's build: sort the binned casters by cell so a query finds a column with one
        /// binary search, and size the candidate scratch for the largest possible query.</summary>
        internal void Seal()
        {
            _cellKeys.AsSpan(0, _binned).Sort(_cellSlots.AsSpan(0, _binned));
            EnsureCapacity(ref _candidates, Count);
            _sealed = true;
        }

        /// <summary>The mesh run a slot added this frame belongs to, as handed to <see cref="Add"/>.</summary>
        internal int RunOf(int slot) => _runs[slot];

        /// <summary>
        /// Write into <paramref name="hits"/> every added caster that touches the light's shadowing shell, in
        /// ascending slot order: exactly what a walk over every added caster keeps. Clears
        /// <paramref name="hits"/> first, and allocates nothing once it has held a frame's worth.
        /// </summary>
        internal void Query(Vector3 lightPosAbsolute, float radius, float nearRadius,
            Vector3 exclusionMin, Vector3 exclusionMax, List<int> hits)
        {
            if (!_sealed) throw new InvalidOperationException("Seal the point caster index before querying it.");
            hits.Clear();
            Span<int> candidates = _candidates.AsSpan(0, GatherCandidates(lightPosAbsolute, radius));
            candidates.Sort();   // the full walk's order, which the signature and the span grouping both rely on
            foreach (int slot in candidates)
            {
                Vector4 sphere = _spheres[slot];
                TouchTests++;
                if (Scene3D.InstanceTouchesLight(new Vector3(sphere.X, sphere.Y, sphere.Z), sphere.W,
                    lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax))
                    hits.Add(slot);
            }
        }

        /// <summary>Copy every caster that could touch the light into the candidate scratch and return how many.
        /// The oversize list always. Binned casters from the columns under the light's grown box, or all of them
        /// when that box is unbinnable or covers more columns than there are binned casters, which is the full walk
        /// and still exact.</summary>
        int GatherCandidates(Vector3 light, float radius)
        {
            int count = 0;
            for (int i = 0; i < _oversizeCount; i++) _candidates[count++] = _oversize[i];
            if (_binned == 0) return count;

            float reach = radius + CellSize + QuerySlack;
            if (!(radius >= 0f)
                || !TryCellRange(light, reach, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
                || (long)(x1 - x0 + 1) * (y1 - y0 + 1) > _binned)
            {
                for (int i = 0; i < _binned; i++) _candidates[count++] = _cellSlots[i];
                return count;
            }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    long last = Pack(x, y, z1);
                    for (int i = LowerBound(Pack(x, y, z0)); i < _binned && _cellKeys[i] <= last; i++)
                        _candidates[count++] = _cellSlots[i];
                }
            return count;
        }

        /// <summary>The first sorted position whose key is at or past <paramref name="key"/>. Hand-written because
        /// a cell holds many casters and <c>BinarySearch</c> may land on any one of them.</summary>
        int LowerBound(long key)
        {
            int low = 0, high = _binned;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_cellKeys[middle] < key) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        static bool TryCellOf(Vector3 centre, out long key)
        {
            key = 0;
            if (!TryCell(centre.X, out int x) || !TryCell(centre.Y, out int y) || !TryCell(centre.Z, out int z))
                return false;
            key = Pack(x, y, z);
            return true;
        }

        // Non-short-circuit on purpose, so every bound is assigned whatever the answer.
        static bool TryCellRange(Vector3 light, float reach,
            out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
            => TryCell(light.X - reach, out x0) & TryCell(light.Y - reach, out y0) & TryCell(light.Z - reach, out z0)
             & TryCell(light.X + reach, out x1) & TryCell(light.Y + reach, out y1) & TryCell(light.Z + reach, out z1);

        static bool TryCell(float metres, out int cell)
        {
            float scaled = MathF.Floor(metres * InverseCellSize);
            // A NaN fails both comparisons, so a non-finite coordinate is refused here too.
            bool packable = scaled >= MinCell && scaled <= MaxCell;
            cell = packable ? (int)scaled : 0;
            return packable;
        }

        static long Pack(int x, int y, int z) =>
            (long)(x + CellBias) << 42 | (long)(y + CellBias) << 21 | (long)(z + CellBias);

        static void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (array.Length < required) Array.Resize(ref array, GrownCapacity(array.Length, required));
        }

        static int GrownCapacity(int current, int required) =>
            Math.Max(required, current > int.MaxValue / 2 ? required : Math.Max(16, current * 2));
    }
}
