using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// How a catalog came to hold an upgrade's content, which is the one fact a version number cannot carry.
/// </summary>
public enum ContentUpgradeDisposition
{
    /// <summary>The runner published the definition's edits. The ledger row is written inside that commit.</summary>
    Applied,

    /// <summary>The content was already present, so nothing was published and the id was recorded as held.</summary>
    Adopted,

    /// <summary>The catalog was seeded from a bundle that already carried the content.</summary>
    Baseline,
}

/// <summary>
/// The three dispositions as the tokens every provider stores, so one spelling of "applied" serves the
/// in-memory store, SQLite and SQL Server and a check constraint can enumerate them.
/// </summary>
public static class ContentUpgradeDispositions
{
    /// <summary>The token <see cref="ContentUpgradeDisposition.Applied"/> is stored as.</summary>
    public const string Applied = "applied";

    /// <summary>The token <see cref="ContentUpgradeDisposition.Adopted"/> is stored as.</summary>
    public const string Adopted = "adopted";

    /// <summary>The token <see cref="ContentUpgradeDisposition.Baseline"/> is stored as.</summary>
    public const string Baseline = "baseline";

    /// <summary>The stored token of one disposition.</summary>
    /// <param name="disposition">The disposition.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is not one of the three.</exception>
    public static string Token(ContentUpgradeDisposition disposition) => disposition switch
    {
        ContentUpgradeDisposition.Applied => Applied,
        ContentUpgradeDisposition.Adopted => Adopted,
        ContentUpgradeDisposition.Baseline => Baseline,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown disposition."),
    };

    /// <summary>One stored token read back.</summary>
    /// <param name="token">The token a ledger row carries.</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> is not one of the three tokens.</exception>
    public static ContentUpgradeDisposition Parse(string token) => token switch
    {
        Applied => ContentUpgradeDisposition.Applied,
        Adopted => ContentUpgradeDisposition.Adopted,
        Baseline => ContentUpgradeDisposition.Baseline,
        _ => throw new ArgumentException(
            FormattableString.Invariant($"'{token}' is not a content upgrade disposition."),
            nameof(token)),
    };
}

/// <summary>
/// ONE ledger row: which upgrade, how the catalog came to hold it, at which version, and who recorded it.
/// </summary>
/// <param name="Id">The definition's stable id, which is the row's primary key.</param>
/// <param name="Order">The definition's order at the time it was recorded.</param>
/// <param name="Disposition">Applied, adopted or baseline.</param>
/// <param name="VersionNumber">The version an applied upgrade published, or the active version for the other two.</param>
/// <param name="Actor">What the engine authenticated, at most 128 characters.</param>
/// <param name="Operator">The identity the console forwarded, empty when it forwarded none.</param>
/// <param name="RecordedAtUtc">When the row was written.</param>
public sealed record ContentUpgradeRecord(
    string Id,
    int Order,
    ContentUpgradeDisposition Disposition,
    int VersionNumber,
    string Actor,
    string Operator,
    DateTimeOffset RecordedAtUtc);
