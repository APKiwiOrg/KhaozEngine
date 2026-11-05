using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Pure consumer policy evaluation shared by publication and historical version selection.</summary>
public static class ContentPublishPolicyEvaluation
{
    /// <summary>
    /// Evaluates only the host policy on detached row views. It does not run engine or history validation.
    /// Publication runs those validators first. An activation supplies complete published snapshots.
    /// </summary>
    /// <param name="candidate">The proposed rows.</param>
    /// <param name="previous">The current rows, or null for an initial publication.</param>
    /// <param name="registry">The registry describing their fields, never supplied to the callback.</param>
    /// <param name="policy">The host-owned policy.</param>
    public static ContentValidationReport Validate(
        ContentSnapshot candidate,
        ContentSnapshot? previous,
        ContentTypeRegistry registry,
        IContentPublishPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);

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

        var combined = new List<ContentFinding>(own.Count);
        bool valid = true;
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
