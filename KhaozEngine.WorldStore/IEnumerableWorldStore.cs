using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.WorldStore;

/// <summary>Optional capability on an <see cref="IWorldStore"/>: list stored keys. A store that cannot enumerate
/// (some remote KV backends) simply does not implement it; consumers feature-detect with
/// <c>store is IEnumerableWorldStore</c>. Order is unspecified.</summary>
public interface IEnumerableWorldStore
{
    /// <summary>Streams every stored entry, optionally restricted to keys beginning with
    /// <paramref name="keyPrefix"/> (null/empty = all).</summary>
    IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(string? keyPrefix = null, CancellationToken cancellationToken = default);
}
