using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The pure (GPU-free) slot-map backing <see cref="Scene3D"/>'s mesh storage: generations + a free-list,
    /// returning indices. <see cref="Scene3D"/> owns the parallel GPU <c>Mesh[]</c> and keys it by the index this
    /// returns. Generations make a freed-then-reused slot give a NEW handle, so a stale handle (held after
    /// <see cref="Free"/>) fails <see cref="IsValid"/> instead of silently aliasing the new occupant.
    /// Headless-unit-testable.
    /// </summary>
    internal sealed class MeshSlotMap
    {
        // Per-slot current generation (>=1 when live; bumped on each free so a stale handle never matches).
        readonly List<int> _generations = new();
        readonly List<bool> _free = new();
        readonly Stack<int> _freeList = new();

        /// <summary>Number of slots ever allocated (live + freed). Indices range [0, <see cref="SlotCount"/>).</summary>
        public int SlotCount => _generations.Count;

        /// <summary>
        /// Allocate a slot: reuse a freed index if one exists (bumping its generation), else append a fresh slot
        /// (generation 1). Returns the slot index; <paramref name="generation"/> is that slot's new generation
        /// (always &gt;= 1, so a <c>default</c> handle with generation 0 is never valid).
        /// </summary>
        public int Alloc(out int generation)
        {
            if (_freeList.Count > 0)
            {
                int index = _freeList.Pop();
                _free[index] = false;
                generation = _generations[index];   // already bumped on Free
                return index;
            }

            int newIndex = _generations.Count;
            _generations.Add(1);
            _free.Add(false);
            generation = 1;
            return newIndex;
        }

        /// <summary>True if <paramref name="index"/>/<paramref name="generation"/> names a live slot (in range,
        /// not freed, matching generation). A <c>default</c> handle (generation 0) is never valid.</summary>
        public bool IsValid(int index, int generation)
        {
            if (generation == 0) return false;
            if (index < 0 || index >= _generations.Count) return false;
            return !_free[index] && _generations[index] == generation;
        }

        /// <summary>
        /// Free the slot named by <paramref name="index"/>/<paramref name="generation"/>: bump its generation
        /// (invalidating any outstanding handle) and push it to the free-list for reuse. Throws
        /// <see cref="ArgumentException"/> on a stale/invalid handle (double-free or bogus); the caller treats a
        /// <c>default</c> handle as a no-op before calling here.
        /// </summary>
        public void Free(int index, int generation)
        {
            if (!IsValid(index, generation))
                throw new ArgumentException(
                    $"UnloadMesh: handle (index {index}, generation {generation}) is stale or invalid (double-free or bogus).");
            _generations[index]++;   // a wrapped/0 generation can't collide: 0 is reserved for default
            if (_generations[index] == 0) _generations[index] = 1;
            _free[index] = true;
            _freeList.Push(index);
        }
    }
}
