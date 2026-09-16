using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>KEC0100</c> band's sweep, spec 8.9's twelve checks plus the two weight bounds, over one
/// candidate and the previous published snapshot or null. It is the band's whole public face and it is what
/// <see cref="InstanceContentValidator"/> hands to the catalog's pass 6.
/// <para>
/// It is PURE, exactly as the catalog's own sweep is. No logging, no counters, no mutation of the
/// candidate, no ambient read and no throwing for a content reason. It ACCUMULATES, so one run reports
/// every defect rather than the earliest, and every check is VACUOUS over an empty row set: nothing here
/// divides by a count or indexes into one.
/// </para>
/// <para>
/// <b>The previous snapshot is an ARGUMENT and the three publish-only checks are skipped when it is
/// null.</b> That is a property of the argument rather than a mode flag, which is what keeps one
/// implementation honest across boot, publish and a test.
/// </para>
/// <para>
/// <b>The catalog's <c>IContentValidator</c> seam carries no previous snapshot</b>, so a sweep reached
/// through pass 6 runs with a null previous and the three publish-only checks stay quiet there. A publish
/// path that holds the previous version calls this method directly and gets all fourteen codes.
/// </para>
/// </summary>
public static class InstanceContentChecks
{
    /// <summary>Runs every check of the band, adding one finding per defect.</summary>
    /// <param name="candidate">The complete candidate, which a publish builds and a boot decodes.</param>
    /// <param name="previous">The previous published snapshot, or null at boot and in most tests.</param>
    /// <param name="findings">The accumulating list, which this method only ever adds to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> or <paramref name="findings"/> is null.</exception>
    public static void Run(
        IContentSnapshot candidate,
        IContentSnapshot? previous,
        ICollection<ContentFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(findings);

        ModFamilyChecks.Run(candidate, previous, findings);
        RarityAndUniqueChecks.Run(candidate, previous, findings);
        CurrencyAndSocketChecks.Run(candidate, findings);
        RareNameCoverageCheck.Run(candidate, findings);
        WeightBoundsChecks.Run(candidate, findings);
    }

    /// <summary>One of the band's eighteen type ids as the catalog's own value type.</summary>
    internal static ContentTypeId Type(ushort typeId) => new(typeId);

    /// <summary>
    /// One field's number, or null when the row is shorter than its schema or the field is absent. A short
    /// row is a <c>KEC0005</c> for every required field it dropped, reported by the catalog's schema pass,
    /// and every check here reads it as absent rather than walking off the end.
    /// </summary>
    internal static long? Number(ContentRow row, int index)
    {
        if (index < 0 || index >= row.Fields.Count)
        {
            return null;
        }

        ContentFieldValue value = row.Fields[index];
        return value.IsAbsent ? null : value.Number;
    }

    /// <summary>True when the snapshot carries that row and it is not retired at this version.</summary>
    internal static bool IsLive(IContentSnapshot snapshot, ushort typeId, long id)
        => id is > 0 and <= int.MaxValue
            && snapshot.TryGetRow(Type(typeId), (int)id, out _)
            && !snapshot.IsRetired(Type(typeId), (int)id);

    /// <summary>
    /// Every LIVE row of one type, in id order. A retired row keeps its bytes forever so a stored item
    /// still decodes, and the content it pointed at is usually retired beside it, so the band follows the
    /// catalog's own pass 3 and does not sweep one.
    /// </summary>
    internal static List<ContentRow> LiveRows(IContentSnapshot snapshot, ushort typeId)
    {
        IReadOnlyList<ContentRow> rows = snapshot.Rows(Type(typeId));
        var live = new List<ContentRow>(rows.Count);
        foreach (ContentRow row in rows)
        {
            if (!row.IsRetired)
            {
                live.Add(row);
            }
        }

        return live;
    }

    /// <summary>
    /// <c>KEC0100</c> on one child row's parent reference. A reference of 0 or an absent one is reported
    /// too: a parent field is what makes a child a child, so unlike an ordinary optional reference there is
    /// no reading of "no content" for it.
    /// </summary>
    internal static void CheckParent(
        IContentSnapshot candidate,
        ICollection<ContentFinding> findings,
        ContentRow row,
        string childTypeKey,
        int fieldIndex,
        string fieldName,
        ushort parentTypeId,
        string parentTypeKey)
    {
        long parentId = Number(row, fieldIndex) ?? 0;
        if (IsLive(candidate, parentTypeId, parentId))
        {
            return;
        }

        findings.Add(new ContentFinding(
            row.Type,
            row.Id,
            InstanceContentFindings.ParentUnresolved,
            InstanceContentFindings.ParentMissing(childTypeKey, row.Id, fieldName, parentTypeKey, parentId)));
    }
}

/// <summary>
/// The band's own <see cref="IContentValidator"/>, registered against the lowest type id of the Instances
/// band by <see cref="InstanceContentTypes.Register"/> and run by the catalog sweep's pass 6.
/// <para>
/// <b>It ignores its <c>type</c> argument on purpose.</b> Every check of spec 8.9 is CROSS TYPE, over a
/// parent and its children or over rarities, words and tags together, so the band is one sweep rather than
/// eighteen. Pass 6 walks the band ascending and only one registration carries this, so the sweep runs
/// exactly once.
/// </para>
/// <para>
/// It carries NO state and reads nothing ambient, so one instance is safe to share across every registry a
/// process builds.
/// </para>
/// </summary>
public sealed class InstanceContentValidator : IContentValidator
{
    /// <inheritdoc />
    /// <remarks>
    /// The seam carries no previous snapshot, so the three publish-only checks do not run here and
    /// <c>KEC0000</c> is what says so. A publish that holds the previous version calls
    /// <see cref="InstanceContentChecks.Run"/> directly.
    /// </remarks>
    public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        => InstanceContentChecks.Run(candidate, previous: null, findings);
}
