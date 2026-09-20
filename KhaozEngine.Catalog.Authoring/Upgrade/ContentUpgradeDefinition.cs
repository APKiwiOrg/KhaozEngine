using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// ONE shipped upgrade: its stable id, the order it runs in, a line an operator reads, and the PLANNER that
/// turns a catalog into the edits that bring it forward.
/// <para>
/// <b>The planner is a pure function of its context.</b> It reads the baseline bundle and the registry and it
/// performs no I/O against the store, so the same catalog always plans the same way and a preview is exactly
/// the plan an apply would carry out. The runner owns every read and every write.
/// </para>
/// <para>
/// <b>The id is stable forever and is never reused</b>, because it is the ledger's primary key and the only
/// thing that answers "did this upgrade already run here". Renaming one re-runs it against every catalog in
/// the field.
/// </para>
/// </summary>
public sealed class ContentUpgradeDefinition
{
    /// <summary>Builds one definition, validating the id and the order at the boundary.</summary>
    /// <param name="id">The stable id, 1 to 128 characters, compared ordinally.</param>
    /// <param name="order">The order within the set, strictly positive and unique.</param>
    /// <param name="description">One line an operator reads, which the report renders beside the id.</param>
    /// <param name="plan">The planner, a pure function of the context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> or <paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or longer than 128 characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is not positive.</exception>
    public ContentUpgradeDefinition(
        string id,
        int order,
        string description,
        Func<ContentUpgradeContext, ContentUpgradePlan> plan)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(plan);

        Stamp = new ContentUpgradeStamp(id, order);
        Description = description;
        Plan = plan;
    }

    /// <summary>The id and the order as the ledger records them, which is what a publish is stamped with.</summary>
    public ContentUpgradeStamp Stamp { get; }

    /// <summary>The stable id, which is <see cref="ContentUpgradeStamp.Id"/> under a shorter name.</summary>
    public string Id => Stamp.Id;

    /// <summary>The order within the set, ascending, unique and strictly positive.</summary>
    public int Order => Stamp.Order;

    /// <summary>One line an operator reads. It is developer output rather than player-facing text.</summary>
    public string Description { get; }

    /// <summary>The planner. It reads the context and returns one of the three plan shapes.</summary>
    public Func<ContentUpgradeContext, ContentUpgradePlan> Plan { get; }
}
