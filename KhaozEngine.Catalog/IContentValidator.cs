using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// One content type's own checks, run AFTER the engine's own (contracts 4.4). A game may register one for
/// an engine type, and it may only ADD a constraint, never relax one.
/// <para>
/// A validator is PURE: no side effects, no logging, no counters, no ambient reads and no throwing for a
/// content reason. It takes its whole world as arguments and ACCUMULATES findings rather than stopping at
/// the first, which is what lets one pass report every defect instead of the earliest.
/// </para>
/// </summary>
public interface IContentValidator
{
    /// <summary>Adds a finding for every defect this type's rules see in the candidate.</summary>
    void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings);
}
