using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>An immutable catalog identity and the two selectors an operator activation checked.</summary>
/// <param name="StoreEpoch">The catalog identity, so a replacement catalog cannot reuse an old check.</param>
/// <param name="ActiveVersion">The active published version.</param>
/// <param name="PinnedVersion">The operator's hold, or null.</param>
public readonly record struct ContentVersionSelection(string StoreEpoch, int ActiveVersion, int? PinnedVersion)
{
    /// <summary>The operator-selected version, falling back to active when no hold exists.</summary>
    public int EffectiveVersion => PinnedVersion ?? ActiveVersion;
}

/// <summary>Optional atomic confirmation of an operator version selection after its policy check.</summary>
public interface IContentVersionSelectionStore
{
    /// <summary>The selection cannot be verified safely, or it moved before its conditional write.</summary>
    const string UnavailableCode = "KEC0045";

    /// <summary>
    /// Compares epoch, active version and pin under the same gate or transaction as the hold and audit
    /// write. Returns false without writing anything when the selection differs. Trusted direct setters
    /// retain their existing semantics and do not use this boundary.
    /// </summary>
    Task<bool> TrySetPinnedVersionAsync(
        int? version,
        ContentVersionSelection expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default);
}
