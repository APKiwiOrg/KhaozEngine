using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>What one corpus world showed when every boundary question was answered twice on one lease: once by the
/// full solve and once with the capture certificate allowed.</summary>
internal sealed class WaterBoundaryParityReport(string name)
{
    internal string Name { get; } = name;
    internal int Questions { get; set; }
    internal int Comparisons { get; set; }
    internal int Certified { get; set; }
    internal Dictionary<WaterTraversalMode, int> CertifiedByMode { get; } = [];
    internal Dictionary<MovementAvailability, int> FullSolve { get; } = [];
    /// <summary>Differences of the listed kinds: the full solve refused and the certificate answered Known.</summary>
    internal Dictionary<MovementAvailability, int> Listed { get; } = [];
    internal List<string> Unlisted { get; } = [];
    internal int KnownContacts { get; set; }
    internal List<string> UncollectedContacts { get; } = [];
    internal int TwoLevelQuestions { get; set; }

    public override string ToString() => $"{Name}: questions={Questions} comparisons={Comparisons} " +
        $"certified={Certified} ({string.Join(", ", CertifiedByMode.Select(p => $"{p.Key}={p.Value}"))}) " +
        $"full=[{string.Join(", ", FullSolve.Select(p => $"{p.Key}={p.Value}"))}] " +
        $"listed=[{string.Join(", ", Listed.Select(p => $"{p.Key}={p.Value}"))}] unlisted={Unlisted.Count} " +
        $"known-contacts={KnownContacts} uncollected={UncollectedContacts.Count} two-level={TwoLevelQuestions}";
}

internal static class WaterBoundaryParityRun
{
    static readonly WaterTraversalMode[] Modes =
        [WaterTraversalMode.SurfaceSwimmer, WaterTraversalMode.WadeOnly, WaterTraversalMode.DryOnly];
    // Movement asks with no proximity, the resolver with the coverage skin.
    static readonly float[] Proximities = [0, MovementQueryLease.CoverageSkinMetres];

    internal static WaterBoundaryParityReport Run(string name)
    {
        WaterBoundaryParityWorld world = WaterBoundaryParityCorpus.Create(name);
        var report = new WaterBoundaryParityReport(name);
        foreach (var (origin, recorded) in world.Groups)
        {
            using var parity = new WaterBoundaryParityLease(world.Field, WaterBoundaryParityCorpus.World, origin,
                world.Minimum, world.Maximum, world.Tuning, world.Select);
            Vector3 physicsOrigin = parity.Frame.PhysicsOrigin;
            foreach (MovementMediumSweepQuery query in recorded)
            {
                report.Questions++;
                foreach (WaterTraversalMode mode in Modes)
                    foreach (float proximity in Proximities)
                        Compare(report, parity.Lease, query, world.Tuning, mode, proximity);
                if (world.Sampler is { } sampler) CheckContacts(report, sampler, query, physicsOrigin);
            }
        }
        return report;
    }

    static void Compare(WaterBoundaryParityReport report, MovementQueryLease lease, MovementMediumSweepQuery query,
        in MoveTuning tuning, WaterTraversalMode mode, float proximity)
    {
        Span<Vector3> fullNormals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        Span<Vector3> certifiedNormals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        float halfHeight = tuning.CapsuleHalfHeight, enter = tuning.SwimEnterDepthFraction;
        MovementAvailability full = MovementWaterBoundary.FindForParityTests(query.Body, query.Delta, halfHeight, enter,
            mode, lease, fullNormals, out int fullCount, out var fullHit, proximity, certify: false, out bool fullCertified);
        MovementAvailability answer = MovementWaterBoundary.FindForParityTests(query.Body, query.Delta, halfHeight, enter,
            mode, lease, certifiedNormals, out int count, out var hit, proximity, certify: true, out bool certified);
        report.Comparisons++;
        report.FullSolve[full] = report.FullSolve.GetValueOrDefault(full) + 1;
        if (fullCertified) report.Unlisted.Add($"{mode} {proximity}: the full solve reported a certificate answer");
        if (certified)
        {
            report.Certified++;
            report.CertifiedByMode[mode] = report.CertifiedByMode.GetValueOrDefault(mode) + 1;
        }
        if (Same(full, fullNormals[..fullCount], fullHit, answer, certifiedNormals[..count], hit)) return;
        // The listed kinds: the full solve refused (precision, numerical, capacity or validation) and the
        // certificate answered Known with no constraint.
        if (certified && answer == MovementAvailability.Known && count == 0 && hit == default &&
            full is MovementAvailability.PrecisionExceeded or MovementAvailability.Unresolved or
                MovementAvailability.CapacityExceeded or MovementAvailability.Invalid)
        {
            report.Listed[full] = report.Listed.GetValueOrDefault(full) + 1;
            return;
        }
        report.Unlisted.Add($"{mode} proximity {proximity} {Describe(query)}: full {full} {fullCount} {fullHit}, " +
            $"certified {certified} {answer} {count} {hit}");
    }

    static bool Same(MovementAvailability a, ReadOnlySpan<Vector3> aNormals, MovementWaterBoundary.Hit aHit,
        MovementAvailability b, ReadOnlySpan<Vector3> bNormals, MovementWaterBoundary.Hit bHit)
    {
        if (a != b || aNormals.Length != bNormals.Length || aHit.Blocked != bHit.Blocked ||
            aHit.InitialOverlap != bHit.InitialOverlap ||
            BitConverter.DoubleToInt64Bits(aHit.Distance) != BitConverter.DoubleToInt64Bits(bHit.Distance)) return false;
        for (int i = 0; i < aNormals.Length; i++)
            if (Bits(aNormals[i]) != Bits(bNormals[i])) return false;
        return true;
    }

    static (int, int, int) Bits(Vector3 value) => (BitConverter.SingleToInt32Bits(value.X),
        BitConverter.SingleToInt32Bits(value.Y), BitConverter.SingleToInt32Bits(value.Z));

    // Every contact a Known producer trace reports is free surface, carries its own column's top as its level and
    // lies in a column the reach collection counted. The certificate rests on these facts.
    static void CheckContacts(WaterBoundaryParityReport report, TileMediumSampler sampler, MovementMediumSweepQuery query,
        Vector3 origin)
    {
        var spans = new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans];
        var contacts = new MovementDomainContact[MovementQueryLease.MaxDomainContacts];
        var trace = sampler.TraceWater(query, origin, MovementWaterFieldTests.Identity, spans, contacts);
        TileWaterColumn[]? columns = sampler.ReachColumnsForTests(query, origin);
        if (columns is not null && columns.Select(c => BitConverter.SingleToInt32Bits(c.Maximum.Y)).Distinct().Count() > 1)
            report.TwoLevelQuestions++;
        if (trace.Availability != MovementAvailability.Known) return;
        foreach (MovementDomainContact contact in contacts.AsSpan(0, trace.ContactsWritten))
        {
            report.KnownContacts++;
            bool counted = columns is not null && contact.Interval.UpperIsFreeSurface && columns.Any(column =>
                column.Domain == contact.Domain &&
                BitConverter.SingleToInt32Bits(column.Maximum.Y) == BitConverter.SingleToInt32Bits(contact.Interval.NominalSurfaceY) &&
                contact.IntervalColumnXZ.X >= column.Minimum.X && contact.IntervalColumnXZ.X <= column.Maximum.X &&
                contact.IntervalColumnXZ.Y >= column.Minimum.Z && contact.IntervalColumnXZ.Y <= column.Maximum.Z);
            if (!counted) report.UncollectedContacts.Add($"{Describe(query)} at {origin}: {contact.Domain.LocalId} " +
                $"{contact.IntervalColumnXZ} level {contact.Interval.NominalSurfaceY} free {contact.Interval.UpperIsFreeSurface}");
        }
    }

    static string Describe(MovementMediumSweepQuery query) =>
        $"centre {query.Body.Centre:R} radius {query.Body.Radius:R} half {query.Body.HalfHeight:R} delta {query.Delta:R}";
}
