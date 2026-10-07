using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Optional atomic release of a draft freeze scoped to the base version of one publish attempt.
/// A completed or refused publish must not release a later version's draft while finishing its cleanup.
/// </summary>
public interface IContentDraftFreezeStore
{
    /// <summary>
    /// Clears the marker only when it still names <paramref name="expectedBaseVersion"/>.
    /// The comparison and release share one gate or transaction. A marker for a later base is left untouched.
    /// This scopes generations, not publishers sharing the same draft and base.
    /// </summary>
    /// <param name="expectedBaseVersion">The base version this attempt froze.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
    Task ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken = default);
}
