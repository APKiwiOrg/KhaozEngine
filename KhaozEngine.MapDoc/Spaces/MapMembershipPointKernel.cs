using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Value-only occupied interval. Cold adapters materialize its released provenance after selection.</summary>
internal readonly record struct MapMembershipPart(MapSpaceDoc Space, string Owner, MapExactValue Lower,
    MapExactValue? Upper, MapSpaceBoundsPart? Cold = null, int LowerSource = -1, int UpperSource = -1,
    int Parent = -1, int Prism = -1, MapLegacyCellTag? Compatibility = null, string? OccupiedSpaceId = null);

internal interface IMapMembershipPointInput
{
    MapPrismDomain[] Prisms { get; }
    int CandidateCount { get; }
    int PortalCount { get; }
    int MaxDepth { get; }
    bool PruneAncestors { get; }
    MapMembershipStatus FailureStatus { get; }
    MapMembershipPart? Portal(int index);
    MapMembershipPart? Candidate(int index);
    void ValidatePrism(int index);
    string PrismOwner(int index);
    string PrismParent(int index);
    bool PrismContains(int index);
    (MapExactValue Lower, MapExactValue Upper) PrismHeights(int index);
    MapMembershipPart Child(int prism, int parentIndex, MapMembershipPart parent, MapExactValue lower,
        MapExactValue upper, bool parentLow, bool parentHigh);
    int Direction(MapMembershipPart part, bool upper);
    bool Ancestor(string ancestor, string child);
    IReadOnlyList<MapSpaceDoc> Ancestors(MapSpaceDoc space);
    string Owner(MapSpaceDoc space);
    MapMembershipPart Carve(MapMembershipPart part, MapExactValue lower, MapExactValue? upper,
        MapMembershipPart? lowCut, MapMembershipPart? highCut);
}

/// <summary>Reusable work arrays. Capacity is a topology bound, independent of observer position.</summary>
internal sealed class MapMembershipPointScratch
{
    internal MapMembershipPart[] Raw, Included;
    internal int[] Cuts;
    internal readonly byte[] Prisms;
    internal bool[] Removed;
    readonly bool _canGrow;
    internal int RawCount, IncludedCount;
    internal MapMembershipPart? Selected;
    internal MapMembershipStatus Status;
    internal string? Detail;
    internal MapMembershipPointScratch(int capacity, int prisms, bool canGrow = false)
    {
        _canGrow = canGrow;
        Raw = new MapMembershipPart[capacity]; Included = new MapMembershipPart[capacity];
        Cuts = new int[capacity]; Prisms = new byte[prisms]; Removed = new bool[capacity];
    }
    internal static long Bytes(int capacity, int prisms) => checked(256L +
        capacity * (2L * System.Runtime.CompilerServices.Unsafe.SizeOf<MapMembershipPart>() + sizeof(int) + sizeof(bool)) + prisms);
    internal void EnsureRawCapacity()
    {
        if (RawCount < Raw.Length) return;
        if (!_canGrow) throw new InvalidOperationException("Admitted membership scratch exceeded.");
        int capacity = checked(Raw.Length * 2);
        Array.Resize(ref Raw, capacity); Array.Resize(ref Included, capacity);
        Array.Resize(ref Cuts, capacity); Array.Resize(ref Removed, capacity);
    }
    internal void Reset()
    {
        Array.Clear(Raw, 0, RawCount); Array.Clear(Included, 0, IncludedCount); Array.Clear(Removed, 0, IncludedCount); Array.Clear(Prisms);
        RawCount = 0; IncludedCount = 0; Selected = null; Status = MapMembershipStatus.Outside; Detail = null;
    }
}

/// <summary>The shared released visiting order, prism clipping, carving, owner deduplication and ancestor pruning.</summary>
internal static class MapMembershipPointKernel
{
    internal static void Query(IMapMembershipPointInput input, MapExactValue y, MapMembershipPointScratch scratch)
    {
        scratch.Reset();
        for (int i = 0; i < input.PortalCount; i++)
        {
            var portal = input.Portal(i);
            if (input.FailureStatus != MapMembershipStatus.Resolved) { scratch.Status = input.FailureStatus; return; }
            if (portal is { } band && Contains(band, y))
            { scratch.Selected = band; scratch.Status = MapMembershipStatus.Resolved; return; }
        }
        bool nested = input.Prisms.Length != 0;
        for (int i = 0; i < input.CandidateCount; i++)
        {
            var candidate = input.Candidate(i);
            if (input.FailureStatus != MapMembershipStatus.Resolved) { scratch.Status = input.FailureStatus; return; }
            if (candidate is { } part && (nested
                ? part.Upper is not { } top || part.Lower.CompareTo(top) < 0 : Contains(part, y))) AddRaw(part, scratch);
        }
        if (nested)
            for (int i = 0; i < input.Prisms.Length; i++)
                if (!AddPrism(input, i, 0, scratch)) return;
        for (int i = 0; i < scratch.RawCount; i++)
        {
            var part = scratch.Raw[i];
            if (!Contains(part, y)) continue;
            int count = 0;
            if (nested)
                for (int j = 0; j < scratch.RawCount; j++)
                {
                    var child = scratch.Raw[j];
                    if (child.Owner == part.Owner || !input.Ancestor(part.Cold?.SpaceId ?? part.OccupiedSpaceId ?? part.Space.Id, child.Cold?.SpaceId ?? child.OccupiedSpaceId ?? child.Space.Id)) continue;
                    int at = count;
                    while (at > 0 && Before(child, scratch.Raw[scratch.Cuts[at - 1]]))
                    { scratch.Cuts[at] = scratch.Cuts[at - 1]; at--; }
                    scratch.Cuts[at] = j; count++;
                }
            MapExactValue cursor = part.Lower;
            MapMembershipPart? low = null, high = null;
            bool removed = false;
            MapExactValue? ceiling = part.Upper;
            for (int c = 0; c < count; c++)
            {
                var cut = scratch.Raw[scratch.Cuts[c]];
                if (cut.Upper is { } end && end.CompareTo(cursor) <= 0) continue;
                if (ceiling is { } top && cut.Lower.CompareTo(top) >= 0) break;
                if (cut.Lower.CompareTo(y) > 0) { ceiling = cut.Lower; high = cut; break; }
                if (cut.Upper is not { } upper || y.CompareTo(upper) < 0) { removed = true; break; }
                cursor = upper; low = cut;
            }
            if (removed) continue;
            var selected = nested ? input.Carve(part, cursor, ceiling, low, high) : part;
            bool duplicate = false;
            for (int j = 0; j < scratch.IncludedCount; j++) duplicate |= scratch.Included[j].Owner == selected.Owner;
            if (!duplicate) scratch.Included[scratch.IncludedCount++] = selected;
        }
        // Released effective membership removes ancestor owners after first-owner deduplication.
        for (int i = 0; input.PruneAncestors && i < scratch.IncludedCount; i++)
        {
            var entry = scratch.Included[i];
            if (entry.Space is null) continue;
            var ancestors = input.Ancestors(entry.Space);
            for (int a = 1; a < ancestors.Count; a++)
            {
                string owner = input.Owner(ancestors[a]);
                for (int j = 0; j < scratch.IncludedCount; j++)
                    if (scratch.Included[j].Space is not null && scratch.Included[j].Owner == owner)
                        scratch.Removed[j] = true;
            }
        }
        int found = 0;
        for (int i = 0; i < scratch.IncludedCount; i++)
            if (!scratch.Removed[i]) { found++; scratch.Selected = scratch.Included[i]; }
        scratch.Status = found > 1 ? MapMembershipStatus.Ambiguous : found == 1 ? MapMembershipStatus.Resolved : MapMembershipStatus.Outside;
        if (found != 1) scratch.Selected = null;
    }
    static bool Before(MapMembershipPart a, MapMembershipPart b)
    {
        int compare = a.Lower.CompareTo(b.Lower);
        return compare < 0 || compare == 0 && StringComparer.Ordinal.Compare(a.Cold?.SpaceId ?? a.OccupiedSpaceId ?? a.Space.Id, b.Cold?.SpaceId ?? b.OccupiedSpaceId ?? b.Space.Id) < 0;
    }
    static bool Contains(MapMembershipPart part, MapExactValue y) => y.CompareTo(part.Lower) >= 0 &&
        (part.Upper is not { } end || y.CompareTo(end) < 0);
    static void AddRaw(MapMembershipPart part, MapMembershipPointScratch scratch)
    {
        scratch.EnsureRawCapacity();
        scratch.Raw[scratch.RawCount++] = part;
    }
    static bool AddPrism(IMapMembershipPointInput input, int index, int depth, MapMembershipPointScratch scratch)
    {
        if (scratch.Prisms[index] == 2) return true;
        if (DemandDiagnostics.Check(null, "MapMembershipPointKernel.MaxRecordDepth.1", "MaxRecordDepth", 0, depth, input.MaxDepth, depth > input.MaxDepth, CapacityDemandLifetime.Transient))
        { scratch.Status = MapMembershipStatus.CapacityExceeded; scratch.Detail = "capacity exceeded: prism parent depth"; return false; }
        if (scratch.Prisms[index] == 1)
        { scratch.Status = MapMembershipStatus.Invalid; scratch.Detail = "invalid prism parent cycle"; return false; }
        scratch.Prisms[index] = 1;
        input.ValidatePrism(index);
        string parent = input.PrismParent(index);
        for (int i = 0; i < input.Prisms.Length; i++)
            if (input.PrismOwner(i) == parent && !AddPrism(input, i, depth + 1, scratch)) return false;
        if (input.PrismContains(index))
        {
            bool anyParent = false;
            int count = scratch.RawCount;
            for (int i = 0; i < count; i++)
            {
                var part = scratch.Raw[i];
                if (part.Owner != parent) continue;
                anyParent = true;
                var (low, high) = input.PrismHeights(index);
                var lower = part.Lower.CompareTo(low) >= 0 ? part.Lower : low;
                var upper = part.Upper is { } top && top.CompareTo(high) <= 0 ? top : high;
                if (lower.CompareTo(upper) >= 0) continue;
                bool parentLow = part.Lower.CompareTo(low) > 0 || part.Lower == low && input.Direction(part, false) >= 0;
                bool parentHigh = part.Upper is { } ceiling && (ceiling.CompareTo(high) < 0 || ceiling == high && input.Direction(part, true) <= 0);
                AddRaw(input.Child(index, i, part, lower, upper, parentLow, parentHigh), scratch);
            }
            if (!anyParent)
            { scratch.Status = MapMembershipStatus.MissingGeometry; scratch.Detail = "missing geometry: prism parent footprint"; return false; }
        }
        scratch.Prisms[index] = 2;
        return true;
    }
}
