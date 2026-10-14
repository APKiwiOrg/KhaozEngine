using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>The exact vertical extent of one occupied space at an XZ. <see cref="Upper"/> is null for an open top. A
/// boundary id is the bound's surface id, else its opening record id, else <c>open-top</c>. A point inside a portal
/// band reports the band bottom and top with the band chain record ids.</summary>
public sealed record MapSpaceInterval(MapMembershipStatus Status, string? SpaceId, MapExactValue Lower, MapExactValue? Upper,
    string? LowerBoundaryId, string? UpperBoundaryId, string? Detail, MapReadWitness Witness)
{
    public MapOccupiedFragment? Fragment { get; }
    internal MapSpaceInterval(MapMembershipStatus status, string? spaceId, MapExactValue lower, MapExactValue? upper,
        string? lowerBoundaryId, string? upperBoundaryId, string? detail, MapReadWitness witness, MapOccupiedFragment? fragment)
        : this(status, spaceId, lower, upper, lowerBoundaryId, upperBoundaryId, detail, witness) => Fragment = fragment;

    /// <summary>The boundary id of an open top.</summary>
    internal const string OpenTop = "open-top";
}

/// <summary>One candidate footprint or portal band at an XZ, as membership evaluates it. <see cref="SpaceId"/> is
/// the alias-resolved space membership reports and <see cref="Owner"/> the root that groups peers.</summary>
internal sealed record MapSpaceBoundsPart(string SpaceId, string Owner, MapExactValue Lower, MapExactValue? Upper,
    string LowerBoundaryId, string UpperBoundaryId, bool Portal)
{
    internal MapOccupiedBoundary? LowerBoundary { get; init; }
    internal MapOccupiedBoundary? UpperBoundary { get; init; }
    internal MapOccupiedFragment? Fragment { get; init; }
    internal IReadOnlyList<MapBoundFace> LowerFaces { get; init; } = Array.Empty<MapBoundFace>();
    internal IReadOnlyList<MapBoundFace> UpperFaces { get; init; } = Array.Empty<MapBoundFace>();
    internal MapRecordRef? AuthoredParent { get; init; }
    internal IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
}

/// <summary>Occupied-space intervals read through point membership, so the two can never disagree.</summary>
public static class MapSpaceIntervals
{
    /// <summary>The interval of the space membership resolves at <paramref name="point"/>. A membership failure,
    /// including <see cref="MapMembershipStatus.Outside"/>, keeps its status with no space and no bounds.</summary>
    public static MapSpaceInterval At(MapScopedSurfaces view, MapFramePoint point)
    {
        ArgumentNullException.ThrowIfNull(view);
        MapMembershipResult result = new MapSpaceMembership(view).Query(point, out MapSpaceBoundsPart? selected);
        return selected is null ? Failure(result.Status, result.Detail, result.Witness)
            : Interval(selected with { SpaceId = result.SpaceId! }, result.Witness);
    }

    public static MapSpaceInterval AtExact(MapScopedSurfaces view, MapExactPoint world)
    {
        ArgumentNullException.ThrowIfNull(view);
        var result = new MapSpaceMembership(view).QueryExact(world, out MapSpaceBoundsPart? selected);
        return selected is null ? Failure(result.Status, result.Detail, result.Witness) : Interval(selected with { SpaceId = result.SpaceId! }, result.Witness);
    }

    /// <summary>Every occupied interval at the point's XZ in ascending <see cref="MapSpaceInterval.Lower"/>, under
    /// point membership's rules for every height at once. Owners are alias-normalized and deduplicated, and a portal
    /// band on the XZ replaces the part of the column it covers, earlier bands first, as membership gives bands
    /// priority. Overlapping intervals of distinct owners make the whole column one
    /// <see cref="MapMembershipStatus.Ambiguous"/> entry, and any other failure is one entry with that status. A column
    /// covers every height, so over an acquisition bounded in height it is one
    /// <see cref="MapMembershipStatus.MissingGeometry"/> entry. A column outside every space is empty.</summary>
    public static IReadOnlyList<MapSpaceInterval> Column(MapScopedSurfaces view, MapFramePoint point)
    {
        ArgumentNullException.ThrowIfNull(view);
        MapMembershipResult result = new MapSpaceMembership(view).Column(point, out IReadOnlyList<MapSpaceBoundsPart> parts);
        if (result.Status == MapMembershipStatus.Outside) return Array.Empty<MapSpaceInterval>();
        if (result.Status != MapMembershipStatus.Resolved) return new[] { Failure(result.Status, result.Detail, result.Witness) };
        var bands = new List<MapSpaceBoundsPart>();
        foreach (MapSpaceBoundsPart band in parts.Where(p => p.Portal)) bands.AddRange(Subtract(band, bands));
        var owned = new List<MapSpaceBoundsPart>();
        foreach (MapSpaceBoundsPart candidate in parts.Where(p => !p.Portal))
            foreach (MapSpaceBoundsPart piece in Subtract(candidate, bands))
            {
                // Kept intervals never overlap, so a piece that meets only its own owner widens one interval.
                MapSpaceBoundsPart[] overlapping = owned.Where(o => Overlaps(o, piece)).ToArray();
                if (overlapping.FirstOrDefault(o => o.Owner != piece.Owner) is { } other)
                {
                    string owners = string.Join(", ", new[] { other.Owner, piece.Owner }.Order(StringComparer.Ordinal));
                    return new[] { Failure(MapMembershipStatus.Ambiguous, "ambiguous occupied spaces: " + owners, result.Witness) };
                }
                owned.RemoveAll(o => overlapping.Any(x => ReferenceEquals(x, o)));
                owned.Add(overlapping.Aggregate(piece, Union));
            }
        return bands.Concat(owned).OrderBy(p => p.Lower).Select(p => Interval(MapOccupiedPartitions.Identify(view, p), result.Witness)).ToArray();
    }

    static MapSpaceInterval Interval(MapSpaceBoundsPart part, MapReadWitness witness) => new(MapMembershipStatus.Resolved,
        part.SpaceId, part.Lower, part.Upper, part.LowerBoundaryId, part.UpperBoundaryId, null, witness, part.Fragment);

    static MapSpaceInterval Failure(MapMembershipStatus status, string? detail, MapReadWitness witness) =>
        new(status, null, default, null, null, null, detail, witness);

    /// <summary>The pieces of <paramref name="part"/> outside every claimed band. A cut end takes the cutting band's
    /// boundary id.</summary>
    static IEnumerable<MapSpaceBoundsPart> Subtract(MapSpaceBoundsPart part, IReadOnlyList<MapSpaceBoundsPart> claimed)
    {
        MapExactValue cursor = part.Lower;
        string cursorId = part.LowerBoundaryId;
        MapOccupiedBoundary? cursorBoundary = part.LowerBoundary;
        foreach (MapSpaceBoundsPart band in claimed.OrderBy(b => b.Lower))
        {
            if (band.Upper is { } bandTop && bandTop.CompareTo(cursor) <= 0) continue;
            if (part.Upper is { } top && band.Lower.CompareTo(top) >= 0) break;
            if (band.Lower.CompareTo(cursor) > 0)
                yield return part with { Lower = cursor, LowerBoundaryId = cursorId, Upper = band.Lower, UpperBoundaryId = band.LowerBoundaryId, LowerBoundary = cursorBoundary, UpperBoundary = band.LowerBoundary, Fragment = null };
            if (band.Upper is not { } end) yield break;
            cursor = end;
            cursorId = band.UpperBoundaryId;
            cursorBoundary = band.UpperBoundary;
        }
        if (part.Upper is not { } upper || cursor.CompareTo(upper) < 0)
            yield return part with { Lower = cursor, LowerBoundaryId = cursorId, LowerBoundary = cursorBoundary, Fragment = null };
    }

    static bool Overlaps(MapSpaceBoundsPart a, MapSpaceBoundsPart b) =>
        (a.Upper is not { } aTop || b.Lower.CompareTo(aTop) < 0) && (b.Upper is not { } bTop || a.Lower.CompareTo(bTop) < 0);

    static MapSpaceBoundsPart Union(MapSpaceBoundsPart a, MapSpaceBoundsPart b)
    {
        MapSpaceBoundsPart low = a.Lower.CompareTo(b.Lower) <= 0 ? a : b;
        MapSpaceBoundsPart high = a.Upper is null || (b.Upper is { } bTop && a.Upper.Value.CompareTo(bTop) >= 0) ? a : b;
        return low with { Upper = high.Upper, UpperBoundaryId = high.UpperBoundaryId, UpperBoundary = high.UpperBoundary, Fragment = null };
    }
}
