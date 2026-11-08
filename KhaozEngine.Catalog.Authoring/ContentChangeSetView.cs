using System.Collections;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

// A live view keeps draft reads current without exposing the list through a cast or SyncRoot.
internal sealed class ContentChangeSetView(List<ContentEdit> edits) : IReadOnlyList<ContentEdit>
{
    public int Count => edits.Count;

    public ContentEdit this[int index] => edits[index];

    public IEnumerator<ContentEdit> GetEnumerator() => edits.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
