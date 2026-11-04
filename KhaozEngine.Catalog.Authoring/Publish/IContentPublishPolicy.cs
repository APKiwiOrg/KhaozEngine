using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// A host-owned constraint on operator publication. Implementations compare detached, typed row values
/// and append findings. They must be deterministic and side-effect free. No store or mutable registry is
/// supplied. Trusted seed and upgrade publication does not acquire an operator policy automatically.
/// </summary>
public interface IContentPublishPolicy
{
    /// <summary>A policy threw instead of completing. Its candidate is refused under this engine code.</summary>
    const string FailureCode = "KEC0044";

    /// <summary>Appends refusals after the existing validation sweep, without changing either snapshot.</summary>
    /// <param name="baseline">The published rows, empty at version zero before the first publication.</param>
    /// <param name="candidate">The proposed rows, with provisional new ids during preview.</param>
    /// <param name="findings">The findings to append. Use <see cref="ContentPublishPolicyRow.Finding"/> for row identifiers.</param>
    void Validate(
        ContentPublishPolicySnapshot baseline,
        ContentPublishPolicySnapshot candidate,
        ICollection<ContentFinding> findings);
}
