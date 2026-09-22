using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Where one definition got to in a run.</summary>
public enum ContentUpgradeStepState
{
    /// <summary>Not run. A preview lists every definition after the first as pending, and a refusal or a failure leaves the rest here.</summary>
    Pending,

    /// <summary>
    /// Planned, without saying which disposition an apply would record. No run produces it since a preview
    /// split into <see cref="WouldPublish"/> and <see cref="WouldAdopt"/>, and it stays declared because the
    /// vocabulary is public and a host that renders its own step lines still names it.
    /// </summary>
    Planned,

    /// <summary>Published as its own version, with the ledger row written inside that commit.</summary>
    Applied,

    /// <summary>The content was already present, so the id was recorded as adopted and nothing was published.</summary>
    Adopted,

    /// <summary>The planner refused, or the publish failed and the ledger says it did not land.</summary>
    Refused,

    /// <summary>
    /// A preview's first pending definition, whose apply WOULD publish a new version carrying the change
    /// lines the step holds.
    /// </summary>
    WouldPublish,

    /// <summary>
    /// A preview's first pending definition, whose apply would publish NOTHING: the catalog already carries
    /// what the definition ships, so the apply writes one adopted ledger row and no version.
    /// </summary>
    WouldAdopt,
}

/// <summary>
/// What ONE definition did in a run: which definition, where it got to, the version it published when it
/// published one, and the lines it would change or did change.
/// <para>
/// Every step carries its definition id, because a message that says an upgrade failed without naming which
/// one sends an operator to the wrong place. The report renders the id on every line it writes.
/// </para>
/// </summary>
public sealed class ContentUpgradeStepResult
{
    static readonly string[] NoLines = [];

    ContentUpgradeStepResult(
        ContentUpgradeDefinition definition,
        ContentUpgradeStepState state,
        int? publishedVersion,
        string reason,
        IReadOnlyList<string> changeLines)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Id = definition.Id;
        Order = definition.Order;
        Description = definition.Description;
        State = state;
        PublishedVersion = publishedVersion;
        Reason = reason;
        ChangeLines = changeLines;
    }

    /// <summary>The definition's stable id, which every line naming this step carries.</summary>
    public string Id { get; }

    /// <summary>The definition's order.</summary>
    public int Order { get; }

    /// <summary>The definition's one-line description.</summary>
    public string Description { get; }

    /// <summary>Where the definition got to.</summary>
    public ContentUpgradeStepState State { get; }

    /// <summary>The version an applied definition published, and null for every other state.</summary>
    public int? PublishedVersion { get; }

    /// <summary>Why it was adopted or refused, and empty otherwise.</summary>
    public string Reason { get; }

    /// <summary>The plan's change lines, on an applied step and on a previewed one.</summary>
    public IReadOnlyList<string> ChangeLines { get; }

    /// <summary>
    /// How the catalog came to hold this upgrade, or null when it does not hold it yet. It is the ledger's
    /// own vocabulary, so a step and the row it wrote read the same.
    /// </summary>
    public ContentUpgradeDisposition? Disposition => State switch
    {
        ContentUpgradeStepState.Applied => ContentUpgradeDisposition.Applied,
        ContentUpgradeStepState.Adopted => ContentUpgradeDisposition.Adopted,
        _ => null,
    };

    /// <summary>
    /// The disposition an APPLY of this previewed step would record, and null on every state a preview does
    /// not produce. It is the ledger's own vocabulary, so a preview and the row the apply will write read
    /// the same, which is what tells an operator that an apply publishes nothing.
    /// </summary>
    public ContentUpgradeDisposition? WouldRecord => State switch
    {
        ContentUpgradeStepState.WouldPublish => ContentUpgradeDisposition.Applied,
        ContentUpgradeStepState.WouldAdopt => ContentUpgradeDisposition.Adopted,
        _ => null,
    };

    /// <summary>A definition that has not run, which is every definition after a stop and after a preview's first.</summary>
    /// <param name="definition">The definition.</param>
    public static ContentUpgradeStepResult Pending(ContentUpgradeDefinition definition)
        => new(definition, ContentUpgradeStepState.Pending, null, string.Empty, NoLines);

    /// <summary>
    /// The same, carrying why this definition did not run. A preview names it, because a definition listed
    /// as pending beside one that was planned reads as an omission otherwise.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="reason">Why it did not run, empty when the caller has nothing to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static ContentUpgradeStepResult Pending(ContentUpgradeDefinition definition, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradeStepResult(definition, ContentUpgradeStepState.Pending, null, reason, NoLines);
    }

    /// <summary>
    /// A preview's exact plan for the first pending definition. An already-satisfied definition previews as a
    /// plan with NO change lines, carrying what the planner found as its reason, because a preview writes
    /// nothing and so cannot record the adoption it would perform.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="changeLines">The plan's change lines, empty when the catalog is already satisfied.</param>
    /// <param name="reason">What the planner found, empty when it planned changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changeLines"/> or <paramref name="reason"/> is null.</exception>
    public static ContentUpgradeStepResult Planned(
        ContentUpgradeDefinition definition,
        IReadOnlyList<string> changeLines,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(changeLines);
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradeStepResult(
            definition, ContentUpgradeStepState.Planned, null, reason, changeLines);
    }

    /// <summary>
    /// A preview's first pending definition, whose apply would PUBLISH a new version. The change lines are
    /// the plan's own, so the report says what the version would carry rather than only that one is due.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="changeLines">The plan's change lines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changeLines"/> is null.</exception>
    public static ContentUpgradeStepResult WouldPublish(
        ContentUpgradeDefinition definition,
        IReadOnlyList<string> changeLines)
    {
        ArgumentNullException.ThrowIfNull(changeLines);
        return new ContentUpgradeStepResult(
            definition, ContentUpgradeStepState.WouldPublish, null, string.Empty, changeLines);
    }

    /// <summary>
    /// A preview's first pending definition, whose apply would publish NOTHING and record the id as adopted.
    /// It carries no change lines, because there is no change to make: an apply writes one ledger row.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="reason">What the planner found.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static ContentUpgradeStepResult WouldAdopt(ContentUpgradeDefinition definition, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradeStepResult(
            definition, ContentUpgradeStepState.WouldAdopt, null, reason, NoLines);
    }

    /// <summary>A definition published as its own version.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="publishedVersion">The version the publish assigned.</param>
    /// <param name="changeLines">The plan's change lines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changeLines"/> is null.</exception>
    public static ContentUpgradeStepResult Applied(
        ContentUpgradeDefinition definition,
        int publishedVersion,
        IReadOnlyList<string> changeLines)
    {
        ArgumentNullException.ThrowIfNull(changeLines);
        return new ContentUpgradeStepResult(
            definition, ContentUpgradeStepState.Applied, publishedVersion, string.Empty, changeLines);
    }

    /// <summary>A definition whose content was already present, recorded rather than published.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="reason">What the planner found.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static ContentUpgradeStepResult Adopted(ContentUpgradeDefinition definition, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradeStepResult(definition, ContentUpgradeStepState.Adopted, null, reason, NoLines);
    }

    /// <summary>A definition the planner refused, or whose publish failed with nothing recorded.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="reason">Why, naming the action an operator takes next.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static ContentUpgradeStepResult Refused(ContentUpgradeDefinition definition, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradeStepResult(definition, ContentUpgradeStepState.Refused, null, reason, NoLines);
    }
}
