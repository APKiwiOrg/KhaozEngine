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

        ContentValidationReport own = ContentPublishPolicyEvaluation.Validate(candidate, previous, registry, policy);
        if (own.Findings.Count == 0)
        {
            return report;
        }

        var combined = new List<ContentFinding>(report.Findings);
        combined.AddRange(own.Findings);
        return new ContentValidationReport(report.IsValid && own.IsValid, Array.AsReadOnly(combined.ToArray()));
    }
}
