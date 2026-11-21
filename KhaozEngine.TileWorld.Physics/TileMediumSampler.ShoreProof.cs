#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

// Count-pass evaluation of a proposed capture certificate beside the released dispatch. It reads only the
// immutable field and suspends the work registries so released counters keep their meanings.
public sealed partial class TileMediumSampler
{
    int? _shoreDomainCount;

    internal int ShoreDomainCount => _shoreDomainCount ??= _field.Columns.Select(c => c.Domain).Distinct().Count();

    void ObserveProofCertificate(in MovementMediumSweepQuery query, Vector3 origin)
    {
        bool enabled = TileWaterQueryWork.Enabled;
        var session = TileWaterShoreCounts.Current;
        TileWaterQueryWork.Enabled = false;
        TileWaterShoreCounts.Current = null;
        try
        {
            bool prism = !_field.CanTraceFlat(query, origin);
            Span<int> selected = stackalloc int[MovementQueryLease.MaxDomainContacts];
            int count = _field.PointIndex.Collect(new TileWaterPathBounds(query, origin), selected);
            var (levels, domains, highest) = Describe(selected[..Math.Max(0, count)]);
            ShoreProofCounts.Sampler(prism, count, levels, domains,
                count > 0 ? (double)highest - origin.Y : null,
                count >= 0 && levels <= 1 && ShoreDomainCount <= MovementQueryLease.MaxWaterDomainsPerLease);
        }
        finally
        {
            TileWaterQueryWork.Enabled = enabled;
            TileWaterShoreCounts.Current = session;
        }
    }

    (int Levels, int Domains, float Highest) Describe(ReadOnlySpan<int> selected)
    {
        var levels = new HashSet<int>();
        var domains = new HashSet<MovementDomainKey>();
        float highest = float.NegativeInfinity;
        foreach (int index in selected)
        {
            TileWaterColumn column = _field.Columns[index];
            levels.Add(BitConverter.SingleToInt32Bits(column.Maximum.Y));
            domains.Add(column.Domain);
            highest = MathF.Max(highest, column.Maximum.Y);
        }
        return (levels.Count, domains.Count, highest);
    }

    // For every column, which other levels lie within the given lateral reach of its footprint.
    internal object ShoreCaptureCensus(float reach)
    {
        var space = new MovementSpaceKey(_field.WorldId, "shore-proof-census");
        int flat = 0, nonuniform = 0, singleAll = 0, singleNonuniform = 0, overflow = 0;
        var nonuniformSet = new HashSet<TileWaterColumn>(_field.NonuniformColumns);
        var levelCounts = new SortedDictionary<int, int>();
        Span<int> selected = stackalloc int[MovementQueryLease.MaxDomainContacts];
        foreach (TileWaterColumn column in _field.Columns)
        {
            bool shore = nonuniformSet.Contains(column);
            if (shore) nonuniform++;
            else flat++;
            var centre = (column.Minimum + column.Maximum) * 0.5f;
            float half = MathF.Max(column.Maximum.X - column.Minimum.X, column.Maximum.Z - column.Minimum.Z) * 0.5f;
            float radius = half * MathF.Sqrt(2) + reach;
            var body = new MovementBodyQuery(centre, radius, radius, space, null);
            int count = _field.PointIndex.Collect(new TileWaterPathBounds(new(body, Vector3.Zero), Vector3.Zero), selected);
            if (count < 0) { overflow++; continue; }
            int levels = Describe(selected[..count]).Levels;
            if (shore) levelCounts[levels] = levelCounts.GetValueOrDefault(levels) + 1;
            if (levels > 1) continue;
            singleAll++;
            if (shore) singleNonuniform++;
        }
        return new
        {
            reach, columns = _field.Columns.Length, flat, nonuniform, overflow,
            domains = ShoreDomainCount,
            levels = _field.Columns.Select(c => c.Maximum.Y).Distinct().Count(),
            singleLevelColumns = singleAll, singleLevelNonuniformColumns = singleNonuniform,
            nonuniformByReachableLevels = levelCounts,
            prisms = _field.Prisms.Length, faces = _field.Faces.Length
        };
    }
}
#endif
