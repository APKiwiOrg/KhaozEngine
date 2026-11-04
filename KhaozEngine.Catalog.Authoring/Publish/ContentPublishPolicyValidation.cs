using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Appends host policy findings after the unchanged catalog validator sweep.</summary>
internal static class ContentPublishPolicyValidation
{
    internal static ContentValidationReport Validate(
        ContentSnapshot candidate,
        ContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ContentTypeRegistry registry,
        IContentPublishPolicy? policy)
    {
        ContentValidationReport report = ContentValidator.Validate(candidate, previous, rules, registry);
        if (policy is null)
        {
            return report;
        }

        var own = new List<ContentFinding>();
        try
        {
            policy.Validate(new ContentPublishPolicySnapshot(previous, registry),
                new ContentPublishPolicySnapshot(candidate, registry), own);
        }
        catch (Exception failure)
        {
            own.Add(new ContentFinding(default, 0, IContentPublishPolicy.FailureCode,
                FormattableString.Invariant($"The consumer publish policy threw {failure.GetType().Name}: {failure.Message}")));
        }

        if (own.Count == 0)
        {
            return report;
        }

        var combined = new List<ContentFinding>(report.Findings);
        bool valid = report.IsValid;
        foreach (ContentFinding finding in own)
        {
            ContentFinding identified = finding;
            if (finding.Key is null
                && (candidate.TryGetRow(finding.Type, finding.Id, out ContentRow? row)
                    || previous is not null && previous.TryGetRow(finding.Type, finding.Id, out row)))
            {
                identified = finding with { Key = row.Key.ToString() };
            }

            combined.Add(identified);
            valid &= string.Equals(finding.Code, ContentValidator.InformationalCode, StringComparison.Ordinal);
        }

        // Never return the list a consumer callback could retain and change after this validation.
        return new ContentValidationReport(valid, Array.AsReadOnly(combined.ToArray()));
    }
}
