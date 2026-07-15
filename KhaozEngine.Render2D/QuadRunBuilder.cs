using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Accumulates items tagged with a texture key into submission-ordered <em>runs</em>, coalescing only
    /// <em>consecutive</em> items that share a key (by reference). Unlike grouping globally per key, this
    /// preserves painter's order across textures: interleaved A,B,A draws yield three runs, so a quad drawn
    /// later never jumps ahead of an earlier quad that used a different texture. Pure / headless-testable.
    /// <para>
    /// Backed by a single growable list plus a list of (key, start, count) run ranges into it, not one
    /// <c>List&lt;T&gt;</c> per run: <see cref="Reset"/> only clears both lists (keeping their capacity), so a
    /// caller that calls <see cref="Reset"/> every frame (or mid-frame, e.g. on every scissor change) pays no
    /// steady-state allocation once the lists have grown to the working-set size.
    /// </para>
    /// </summary>
    internal sealed class QuadRunBuilder<T>
    {
        readonly List<T> _items = new();
        readonly List<(object Key, int Start, int Count)> _ranges = new();

        /// <summary>All accumulated items in submission order, backing every run's <c>(Start, Count)</c> slice.</summary>
        public IReadOnlyList<T> Items => _items;

        /// <summary>All accumulated items as a span, for zero-copy upload of a run's slice (<c>AllItems.Slice(Start, Count)</c>).</summary>
        public Span<T> AllItems => CollectionsMarshal.AsSpan(_items);

        /// <summary>The runs in submission order: each is a <c>(Key, Start, Count)</c> slice into <see cref="Items"/>.</summary>
        public IReadOnlyList<(object Key, int Start, int Count)> Runs => _ranges;

        /// <summary>Append <paramref name="item"/>, starting a new run when <paramref name="key"/> differs from the open run.</summary>
        public void Add(object key, T item)
        {
            if (_ranges.Count == 0 || !ReferenceEquals(_ranges[^1].Key, key))
                _ranges.Add((key, _items.Count, 0));
            _items.Add(item);
            var last = _ranges[^1];
            _ranges[^1] = (last.Key, last.Start, last.Count + 1);
        }

        /// <summary>Drop all runs (call at the start of each batch). Keeps backing-list capacity, so steady-state calls allocate nothing.</summary>
        public void Reset()
        {
            _items.Clear();
            _ranges.Clear();
        }

        // Opt-in texture-grouping scratch (see GroupKeysInFirstSeenOrder below). Only touched by callers that
        // enable grouping, and reused across builds (Clear()d, not reallocated), so a caller that never groups
        // pays nothing and a caller that always groups the same working set of keys settles to zero allocation.
        readonly Dictionary<object, List<int>> _groupScratch = new();
        readonly List<object> _groupOrder = new();

        /// <summary>
        /// Buckets this build's runs by key, regardless of submission order, for a caller that opts into
        /// texture-grouping and does not need strict painter's order. Returns the distinct keys in first-seen
        /// order. Look up each key's source run indices (into <see cref="Runs"/>) via
        /// <see cref="RunIndicesForGroup"/>. Submission order is preserved WITHIN a group (its run indices come
        /// back in ascending, i.e. submission, order) but NOT between groups, so a caller may still need to issue
        /// one GPU upload per source run while merging them into a single draw call by uploading to consecutive
        /// destination offsets. Call after the build is complete (before the matching <see cref="Reset"/>).
        /// </summary>
        public IReadOnlyList<object> GroupKeysInFirstSeenOrder()
        {
            foreach (var kv in _groupScratch) kv.Value.Clear();
            _groupOrder.Clear();
            for (int i = 0; i < _ranges.Count; i++)
            {
                object key = _ranges[i].Key;
                if (!_groupScratch.TryGetValue(key, out List<int>? indices))
                {
                    indices = new List<int>();
                    _groupScratch[key] = indices;
                }
                if (indices.Count == 0) _groupOrder.Add(key);
                indices.Add(i);
            }
            return _groupOrder;
        }

        /// <summary>The run indices (into <see cref="Runs"/>) belonging to <paramref name="key"/>, in submission order. Only valid for a key returned by the most recent <see cref="GroupKeysInFirstSeenOrder"/> call.</summary>
        public IReadOnlyList<int> RunIndicesForGroup(object key) => _groupScratch[key];
    }
}
