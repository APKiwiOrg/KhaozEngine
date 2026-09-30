using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Optional whole-version row reads for consumers that compare published catalogs. Stores implementing
/// only <see cref="IContentAuthoringStore"/> remain compatible through its paged row reads.
/// </summary>
public interface IContentVersionRowSource
{
    /// <summary>
    /// Reads every live revision of the types registered by the store, including retired rows, ordered by
    /// type id then definition id. Stored types outside that registry are omitted before decoding their
    /// rows and fields. Revision metadata and field values describe the stored historical rows. Version 0
    /// resolves to the active published version, without applying the open draft. A store with no published version,
    /// or a version with no live rows, returns an empty list. The read is not paged.
    /// </summary>
    /// <param name="versionNumber">The version to read, or 0 for the active version.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ContentRowRevision>> ReadVersionRowsAsync(
        int versionNumber,
        CancellationToken cancellationToken = default);
}
