using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The optional whole-version read over the same temporal rows as the publish baseline.</summary>
public sealed partial class InMemoryContentAuthoringStore : IContentVersionRowSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> ReadVersionRowsAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            List<ContentRowRevision> rows = LiveAt(versionNumber == 0 ? _activeVersion : versionNumber);
            rows.Sort(static (left, right) => left.Row.Type.Value == right.Row.Type.Value
                ? left.Row.Id.CompareTo(right.Row.Id)
                : left.Row.Type.Value.CompareTo(right.Row.Type.Value));
            return Task.FromResult<IReadOnlyList<ContentRowRevision>>(rows);
        }
    }
}
