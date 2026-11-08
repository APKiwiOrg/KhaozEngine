using System.Collections;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

// Unlike ReadOnlyCollection, this view exposes no ICollection.SyncRoot path to its owned array.
internal sealed class CatalogReadOnlyView<T>(T[] items) : IReadOnlyList<T>
{
    readonly T[] _items = items;

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
