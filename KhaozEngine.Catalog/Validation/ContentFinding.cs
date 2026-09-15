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
