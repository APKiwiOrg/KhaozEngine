using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// One validation finding (spec 5.2). The CODE is a stable token so a counter, a test and an operator
/// runbook can all key on it, the same rule decode reason tokens follow. Codes are never reused and never
/// renumbered, which is why a withdrawn code stays withdrawn rather than being recycled.
/// </summary>
/// <param name="Type">The content type the defect is in.</param>
/// <param name="Id">The definition id, or 0 when the finding is about the candidate as a whole.</param>
/// <param name="Code">The stable <c>KEC</c> token.</param>
/// <param name="Message">The human-readable detail, naming the values that failed.</param>
public sealed record ContentFinding(ContentTypeId Type, int Id, string Code, string Message);

/// <summary>
/// What one validation run produced (spec 5.1): whether the candidate may be published or loaded, and
/// every finding the sweep accumulated, in the order the five passes emitted them.
/// <para>
/// The sweep ACCUMULATES rather than stopping at the first defect, following
/// <c>JsonSchemaValidator.ValidationReport</c> and its run-to-the-end shape, so a bulk import reports every
/// bad row in one pass instead of the earliest.
/// </para>
/// <para>
/// <see cref="IsValid"/> is false when the report carries ANY finding except <c>KEC0000</c>, which is
/// informational and says only that the publish-only checks did not run.
/// </para>
/// </summary>
/// <param name="IsValid">True when nothing but <c>KEC0000</c> was found.</param>
/// <param name="Findings">Every finding, in sweep order.</param>
public sealed record ContentValidationReport(bool IsValid, IReadOnlyList<ContentFinding> Findings);
