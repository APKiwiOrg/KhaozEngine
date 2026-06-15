using System.Collections.Generic;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Accumulates items tagged with a texture key into submission-ordered <em>runs</em>, coalescing only
    /// <em>consecutive</em> items that share a key (by reference). Unlike grouping globally per key, this
    /// preserves painter's order across textures: interleaved A,B,A draws yield three runs, so a quad drawn
    /// later never jumps ahead of an earlier quad that used a different texture. Pure / headless-testable.
    /// </summary>
    internal sealed class QuadRunBuilder<T>
    {
        readonly List<(object Key, List<T> Items)> _runs = new();

        /// <summary>The runs in submission order; each run's <c>Items</c> share one texture key.</summary>
        public IReadOnlyList<(object Key, List<T> Items)> Runs => _runs;

        /// <summary>Append <paramref name="item"/>, starting a new run when <paramref name="key"/> differs from the open run.</summary>
        public void Add(object key, T item)
        {
            if (_runs.Count == 0 || !ReferenceEquals(_runs[^1].Key, key))
                _runs.Add((key, new List<T>()));
            _runs[^1].Items.Add(item);
        }

        /// <summary>Drop all runs (call at the start of each batch).</summary>
        public void Reset() => _runs.Clear();
    }
}
