using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using static KhaozEngine.MapDoc.Foliage.MapFoliageDistribution;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Streams stable lattice blocks in Z,X order, merging proposal iterators rather than scanning gaps. Both
/// proposals evaluate every union address at its own jittered world point, regardless of render chunk ownership.</summary>
public static class MapFoliageDifferential
{
    public static MapFoliageDifferentialResult Compare(MapDocument before, MapDocument after, MapAssetClosure assets, string layerId,
        MapFoliageLimits? limits = null, Action<MapFoliageChange>? observe = null)
    {
        MapFoliageContextResult a = MapFoliageGenerationContext.Capture(before, assets);
        if (a.Context is null) return new(a.Status, 0, 0, 0, "", "", "", MapFoliageSupport.NoWork);
        MapFoliageContextResult b = MapFoliageGenerationContext.Capture(after, assets);
        if (b.Context is null) return new(b.Status, 0, 0, 0, "", a.Context.SnapshotId, "", MapFoliageSupport.NoWork);
        return Compare(a.Context, b.Context, layerId, limits, observe);
    }

    public static MapFoliageDifferentialResult Compare(MapFoliageGenerationContext before, MapFoliageGenerationContext after, string layerId,
        MapFoliageLimits? limits = null, Action<MapFoliageChange>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        MapFoliageLimits admitted = Limits(limits);
        var meter = new Meter();
        long added = 0, removed = 0, changed = 0, peak = 0;
        try
        {
            MapFoliageLayer a = Layer(before, layerId), b = Layer(after, layerId);
            MapFoliageLatticeRange ar = Range(a), br = Range(b);
            if (DemandDiagnostics.Check(meter, "MapFoliageDifferential.MaxDifferentialCandidates.1", "MaxDifferentialCandidates", 0, 2 * (ar.Count + br.Count - ar.Intersection(br)), admitted.MaxDifferentialCandidates, 2 * (ar.Count + br.Count - ar.Intersection(br)) > admitted.MaxDifferentialCandidates, CapacityDemandLifetime.Cumulative))
                throw Refuse(MapFoliageStatus.CapacityExceeded, "differential candidates");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Add("kemap/foliage-differential/1"); Add(before.SnapshotId); Add(after.SnapshotId); Add(layerId);
            using IEnumerator<MapFoliageComparisonBlock> ai = ar.Blocks().GetEnumerator(), bi = br.Blocks().GetEnumerator();
            bool hasA = ai.MoveNext(), hasB = bi.MoveNext();
            while (hasA || hasB)
            {
                int order = !hasA ? 1 : !hasB ? -1 : Order(ai.Current, bi.Current);
                MapFoliageComparisonBlock block = order <= 0 ? ai.Current : bi.Current;
                var retained = new Reconciliation(admitted.MaxComparisonRetainedBytes);
                IReadOnlyList<MapFoliageChange> rows;
                try
                {
                    long admittedRows = added + removed + changed;
                    rows = Block(before, after, a, b, ar, br, block, admitted, meter, retained, () =>
                    {
                        if (DemandDiagnostics.Check(meter, "MapFoliageDifferential.MaxChangeRows.1", "MaxChangeRows", admittedRows, 1, admitted.MaxChangeRows, admittedRows++ >= admitted.MaxChangeRows, CapacityDemandLifetime.Cumulative)) throw Refuse(MapFoliageStatus.CapacityExceeded, "change rows");
                        retained.Take(256);
                    });
                    foreach (MapFoliageChange row in rows)
                    {
                        if (DemandDiagnostics.Check(meter, "MapFoliageDifferential.MaxChangeRows.2", "MaxChangeRows", (decimal)added + removed + changed, 1, admitted.MaxChangeRows, added + removed + changed >= admitted.MaxChangeRows, CapacityDemandLifetime.Cumulative)) throw Refuse(MapFoliageStatus.CapacityExceeded, "change rows");
                        switch (row.Kind)
                        {
                            case MapFoliageChangeKind.Added: added++; break;
                            case MapFoliageChangeKind.Removed: removed++; break;
                            case MapFoliageChangeKind.Changed: changed++; break;
                        }
                        Add(((int)row.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Add(row.Before is null ? "absent" : MapFoliageDifferentialCore.Fingerprint(row.Before));
                        Add(row.After is null ? "absent" : MapFoliageDifferentialCore.Fingerprint(row.After));
                        observe?.Invoke(row);
                    }
                }
                finally { peak = Math.Max(peak, retained.Peak); }
                if (order <= 0) hasA = ai.MoveNext();
                if (order >= 0) hasB = bi.MoveNext();
            }
            return new(MapFoliageStatus.Complete, added, removed, changed, Convert.ToHexStringLower(digest.GetHashAndReset()),
                before.SnapshotId, after.SnapshotId, meter.Work(peak));
            void Add(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                Span<byte> length = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                digest.AppendData(length); digest.AppendData(bytes);
            }
        }
        catch (MapFoliageRefusal refusal) { return new(refusal.Status, added, removed, changed, "", before.SnapshotId, after.SnapshotId, meter.Work(peak)); }
    }

    internal static IReadOnlyList<MapFoliageChange> CompareBlock(MapFoliageGenerationContext before, MapFoliageGenerationContext after,
        string layerId, MapFoliageComparisonBlock block, MapFoliageLimits limits)
    {
        MapFoliageLayer a = Layer(before, layerId), b = Layer(after, layerId);
        MapFoliageLimits admitted = Limits(limits);
        var retained = new Reconciliation(admitted.MaxComparisonRetainedBytes);
        var meter = new Meter();
        int rows = 0;
        return Block(before, after, a, b, Range(a), Range(b), block, admitted, meter, retained, () =>
        {
            if (DemandDiagnostics.Check(meter, "MapFoliageDifferential.MaxChangeRows.3", "MaxChangeRows", rows, 1, admitted.MaxChangeRows, rows++ >= admitted.MaxChangeRows, CapacityDemandLifetime.Cumulative)) throw Refuse(MapFoliageStatus.CapacityExceeded, "change rows");
            retained.Take(256);
        });
    }

    static IReadOnlyList<MapFoliageChange> Block(MapFoliageGenerationContext before, MapFoliageGenerationContext after,
        MapFoliageLayer a, MapFoliageLayer b, MapFoliageLatticeRange ar, MapFoliageLatticeRange br, MapFoliageComparisonBlock block,
        MapFoliageLimits limits, Meter meter, Reconciliation retained, Action admitChange)
    {
        // Admit evaluator model arrays, the empty cache/index structures and row-list headers before constructing them.
        retained.Take(checked(4096 + 128L * (a.Models.Count + b.Models.Count)));
        var old = new List<MapFoliageDecision>();
        var next = new List<MapFoliageDecision>();
        var ae = new Evaluation(before, a, limits, meter, retained);
        var be = new Evaluation(after, b, limits, meter, retained);
        long startX = (long)block.X * 64, startZ = (long)block.Z * 64;
        for (long z = startZ; z < startZ + 64; z++)
            for (long x = startX; x < startX + 64; x++)
            {
                if (x < int.MinValue || x > int.MaxValue || z < int.MinValue || z > int.MaxValue ||
                    !ar.Contains((int)x, (int)z) && !br.Contains((int)x, (int)z)) continue;
                if (DemandDiagnostics.Check(meter, "MapFoliageDifferential.MaxDifferentialCandidates.2", "MaxDifferentialCandidates", meter.Candidates, 2, limits.MaxDifferentialCandidates, meter.Candidates > limits.MaxDifferentialCandidates - 2, CapacityDemandLifetime.Cumulative)) throw Refuse(MapFoliageStatus.CapacityExceeded, "differential candidates");
                retained.Take(2048);
                old.Add(ae.Decide((int)x, (int)z, null, Admit));
                next.Add(be.Decide((int)x, (int)z, null, Admit));
            }
        return MapFoliageDifferentialCore.JoinBlock(old, next, admitChange);
        bool Admit(long bytes) { retained.Take(bytes); return true; }
    }

    static MapFoliageLatticeRange Range(MapFoliageLayer layer) => MapFoliageLatticeRange.Of(layer.OriginX, layer.OriginZ,
        layer.OriginX + (layer.Width - 1) * layer.CellSize, layer.OriginZ + (layer.Depth - 1) * layer.CellSize, layer.Spacing);
    static int Order(MapFoliageComparisonBlock a, MapFoliageComparisonBlock b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X);
}
