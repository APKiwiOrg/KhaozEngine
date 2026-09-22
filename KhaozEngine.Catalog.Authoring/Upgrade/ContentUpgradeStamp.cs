using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The identity of one content upgrade as the ledger records it: the definition's stable id and the order it
/// carried when it ran.
/// <para>
/// <b>The id is stable forever and never reused</b>, because it is the primary key of the ledger row and the
/// only thing that answers "did this upgrade already run against this catalog". A version number cannot:
/// it says how many publishes happened, not which upgrades ran.
/// </para>
/// <para>
/// The order is recorded beside it rather than looked up, so a ledger read can sort an existing catalog's
/// history without the shipped definition set. A catalog is allowed to hold an upgrade this build no longer
/// ships, and the row still has to sort.
/// </para>
/// </summary>
public sealed record ContentUpgradeStamp
{
    /// <summary>The longest id the ledger column takes, which is the audit table's actor cap.</summary>
    public const int MaxIdLength = 128;

    /// <summary>Builds one stamp, checking both halves at the boundary rather than at the insert.</summary>
    /// <param name="id">The definition's stable id, 1 to 128 characters, compared ordinally.</param>
    /// <param name="order">The definition's order, which is strictly positive.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or longer than 128 characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is not positive.</exception>
    public ContentUpgradeStamp(string id, int order)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (id.Length > MaxIdLength)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"An upgrade id is at most {MaxIdLength} characters and this one is {id.Length}."),
                nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(order);

        Id = id;
        Order = order;
    }

    /// <summary>The definition's stable id, compared ordinally everywhere.</summary>
    public string Id { get; }

    /// <summary>The definition's order at the time it was recorded, which is what a ledger read sorts on.</summary>
    public int Order { get; }
}
