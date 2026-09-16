using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// One validation finding as a console receives it. The CODE is the stable token an operator runbook, a
/// counter and a test all key on, and the message is the human half that names the values that failed.
/// </summary>
/// <param name="Code">The stable <c>KEC</c> token.</param>
/// <param name="Type">The content type's KEY, or empty when the finding is about the candidate as a whole.</param>
/// <param name="Id">The definition id, or 0 when the finding is about the candidate as a whole.</param>
/// <param name="Message">The human-readable detail.</param>
public sealed record CatalogFindingPayload(string Code, string Type, int Id, string Message);

/// <summary>
/// The OBJECT error payload every mutating action refuses with (spec 10.5), so one console parser reads
/// every refusal. <see cref="Findings"/> is empty when the refusal is about the REQUEST rather than about
/// the content, which keeps the shape the same either way rather than making a console guess which one it
/// received.
/// </summary>
/// <param name="Error">The human-readable refusal, naming both sides of the rule that was broken.</param>
/// <param name="Reason">The stable reason token a log line and a counter key on, never the message.</param>
/// <param name="FindingCount">How many findings the refusal carries.</param>
/// <param name="Findings">Every finding, in the order the sweep produced them, and never just the first.</param>
public sealed record CatalogErrorPayload(
    string Error,
    string Reason,
    int FindingCount,
    IReadOnlyList<CatalogFindingPayload> Findings);

/// <summary>
/// The 409 of optimistic concurrency (spec 10.6), which names BOTH numbers. Two consoles cannot both
/// publish the same draft: the second one's expectation is stale, and a caller that is told what it expected
/// and what the store stands at re-reads and retries rather than guessing.
/// </summary>
/// <param name="Error">The human-readable refusal.</param>
/// <param name="Reason">The stable reason token.</param>
/// <param name="ExpectedBaseVersion">The base version the request expected.</param>
/// <param name="ActualBaseVersion">The base version the store actually stands at.</param>
public sealed record CatalogBaseVersionMovedPayload(
    string Error,
    string Reason,
    int ExpectedBaseVersion,
    int ActualBaseVersion);

/// <summary>
/// A 409 that names the way out. A draft a publish is holding takes no edit and no discard, and a bare
/// refusal would leave an operator with a console answering 409 to everything and no reading of why.
/// </summary>
/// <param name="Error">The human-readable refusal.</param>
/// <param name="Reason">The stable reason token.</param>
/// <param name="Remedy">What an operator does about it.</param>
public sealed record CatalogConflictPayload(string Error, string Reason, string Remedy);

/// <summary>
/// The empty-database refusal of spec 10.9, carrying the version the store stands at. A bundle is imported
/// into an EMPTY database only, and a deployed database's values change through an edit and a publish and
/// through nothing else, ever.
/// </summary>
/// <param name="Error">The human-readable refusal.</param>
/// <param name="Reason">The stable reason token.</param>
/// <param name="ActiveVersion">The version the store already stands at.</param>
public sealed record CatalogNotEmptyPayload(string Error, string Reason, int ActiveVersion);

/// <summary>
/// How a refusal becomes a result, in ONE place: which statuses the authoring store's reason tokens map to,
/// and what the body carries.
/// <para>
/// <b>Three tokens are a 409 and every other refusal is a 400.</b> A base version that moved, a publish in
/// flight and a draft target held under another operation are all races between two consoles rather than bad
/// requests, and a caller resolves each by re-reading and retrying. An unknown type, an unknown field, an
/// unknown row and a malformed body are the caller's own payload, and no retry fixes them.
/// </para>
/// </summary>
internal static class CatalogRefusal
{
    /// <summary>What an operator does about a draft a publish is holding.</summary>
    public const string PublishInProgressRemedy =
        "wait for the publish in flight to finish, then read the draft again. A publish clears the marker on every exit path, and a marker a dead publish left behind is cleared by the next publish's own baseline read.";

    /// <summary>What an operator does about a draft that already holds another intent for the row.</summary>
    public const string EditCollisionRemedy =
        "publish or discard the pending edit for that row first. A draft holds ONE pending intent per row, so an update and a retire of the same row are two publishes.";

    /// <summary>A refusal about the REQUEST rather than about the content, carrying no finding.</summary>
    /// <param name="error">The human-readable refusal.</param>
    public static AdminActionResult Malformed(string error)
        => AdminActionResult.BadRequest(new CatalogErrorPayload(
            error, CatalogRequest.MalformedRequestReason, 0, []));

    /// <summary>A refusal about the request under a reason token of its own.</summary>
    /// <param name="error">The human-readable refusal.</param>
    /// <param name="reason">The stable reason token.</param>
    public static AdminActionResult BadRequest(string error, string reason)
        => AdminActionResult.BadRequest(new CatalogErrorPayload(error, reason, 0, []));

    /// <summary>
    /// A refusal carrying EVERY finding rather than the first, which is what lets an operator fix three
    /// problems in one round trip instead of three.
    /// </summary>
    /// <param name="error">The human-readable refusal.</param>
    /// <param name="reason">The stable reason token.</param>
    /// <param name="findings">The findings, already rendered.</param>
    public static AdminActionResult Findings(
        string error,
        string reason,
        IReadOnlyList<CatalogFindingPayload> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return AdminActionResult.BadRequest(new CatalogErrorPayload(error, reason, findings.Count, findings));
    }

    /// <summary>
    /// The store's own refusal as a result. The three race tokens become a 409 and everything else a 400,
    /// and a refusal the validator produced carries its findings through unchanged.
    /// </summary>
    /// <param name="failure">The store's refusal.</param>
    /// <param name="registry">The registry a finding's type id is named through.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static AdminActionResult From(ContentAuthoringException failure, ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(registry);

        string reason = failure.Reason ?? CatalogRequest.MalformedRequestReason;
        switch (failure.Reason)
        {
            case ContentAuthoringException.PublishInProgressReason:
                return AdminActionResult.Conflict(new CatalogConflictPayload(
                    failure.Message, reason, PublishInProgressRemedy));

            case ContentAuthoringException.EditTargetCollisionReason:
                return AdminActionResult.Conflict(new CatalogConflictPayload(
                    failure.Message, reason, EditCollisionRemedy));

            // The empty-database refusal is NOT here, deliberately: its body carries the version the store
            // stands at, and only the import action has read that number. Fabricating a 0 for it would tell
            // an operator the catalog is at a version nothing ever publishes.
            default:
                return AdminActionResult.BadRequest(new CatalogErrorPayload(
                    failure.Message, reason, failure.Findings.Count, Render(failure.Findings, registry)));
        }
    }

    /// <summary>Every finding rendered, with each type id turned into the key a console renders rows under.</summary>
    /// <param name="findings">The findings, in sweep order.</param>
    /// <param name="registry">The registry the type ids are declared in.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<CatalogFindingPayload> Render(
        IReadOnlyList<ContentFinding> findings,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(registry);

        var rendered = new List<CatalogFindingPayload>(findings.Count);
        for (int i = 0; i < findings.Count; i++)
        {
            ContentFinding finding = findings[i];
            rendered.Add(new CatalogFindingPayload(
                finding.Code,
                registry.TryGet(finding.Type, out ContentTypeRegistration? type) ? type.TypeKey : string.Empty,
                finding.Id,
                finding.Message));
        }

        return rendered;
    }
}
