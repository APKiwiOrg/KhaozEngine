using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Where one definition got to in a run.</summary>
public enum ContentUpgradeStepState
{
    /// <summary>Not run. A preview lists every definition after the first as pending, and a refusal or a failure leaves the rest here.</summary>
    Pending,

    /// <summary>Planned, and the plan's change lines are on the step. A preview's first pending definition only.</summary>
    Planned,

    /// <summary>Published as its own version, with the ledger row written inside that commit.</summary>
    Applied,

    /// <summary>The content was already present, so the id was recorded as adopted and nothing was published.</summary>
    Adopted,

    /// <summary>The planner refused, or the publish failed and the ledger says it did not land.</summary>
    Refused,
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

    /// <summary>A definition that has not run, which is every definition after a stop and after a preview's first.</summary>
    /// <param name="definition">The definition.</param>
    public static ContentUpgradeStepResult Pending(ContentUpgradeDefinition definition)
        => new(definition, ContentUpgradeStepState.Pending, null, string.Empty, NoLines);

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
