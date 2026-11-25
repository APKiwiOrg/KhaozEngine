using System;
using System.Collections.Generic;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Attachments.ChainSheetWorkspace;

namespace KhaozEngine.Attachments;

internal sealed class ChainSheetContact
{
    internal const int MaxPasses = 8;
    private const double MinimumWeight = 1e-12;
    private const double SideTolerance = 2.5e-7;
    private const double MaximumPositionMargin = 2e-6;
    private readonly Band[][] _bandsByDepth;
    private readonly ChainSheetWorkspace _workspace;

    private readonly record struct Band(int FirstChain, int SecondChain, int FirstUpper, int SecondUpper,
        int FirstLower, int SecondLower)
    {
        internal Corners ReadCorners(Vector3[] positions) =>
            new(positions[FirstUpper], positions[SecondUpper], positions[FirstLower], positions[SecondLower]);
    }

    internal ChainSheetContact(ChainRig rig, IReadOnlyList<ChainWidthBounds.Pair> pairs, int depths, int obstacleCount)
    {
        var bands = new List<Band>?[depths];
        // Each width pair is a lower row. A tip pair closes from the last shared joint,
        // which need not be the actual parent of the longer chain's tip.
        var winding = new ChainSheetWinding(rig, pairs);
        foreach (var link in pairs)
        {
            var pair = winding.Orient(link);
            var first = pair.First;
            var second = pair.Second;
            int firstCount = rig.Chains[first.Chain].Joints.Count;
            bool tip = first.Particle - first.Root == firstCount;
            int previous = tip ? Math.Min(firstCount, rig.Chains[second.Chain].Joints.Count) - 1 : pair.Depth - 1;
            int depth = tip ? ChainBoundGroups.TipDepth : pair.Depth;
            (bands[depth] ??= []).Add(new(first.Chain, second.Chain, first.Root + previous,
                second.Root + previous, first.Particle, second.Particle));
        }

        _bandsByDepth = new Band[depths][];
        int maxBands = 0;
        for (int depth = 0; depth < depths; depth++)
        {
            var row = bands[depth]?.ToArray() ?? [];
            _bandsByDepth[depth] = row;
            maxBands = Math.Max(maxBands, row.Length);
        }

        _workspace = new ChainSheetWorkspace(maxBands, obstacleCount);
    }

    internal void ProjectPositions(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ChainSettings> settings, float largestObstacle)
    {
        var bands = _bandsByDepth[depth];
        _workspace.BeginDepth();
        bool useCandidates = CanReuseCandidates;
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            var first = placed[band.FirstChain];
            var second = placed[band.SecondChain];
            var opening = band.ReadCorners(particles.OpeningPositions);
            var current = band.ReadCorners(particles.Positions);
            float radius = Radius(band, settings);
            var reach = ChainPlacementCull.BandReach(first.Start, first.Length, second.Start, second.Length,
                current.FirstUpper, current.SecondUpper, radius, opening.FirstUpper, opening.SecondUpper,
                opening.FirstLower, opening.SecondLower);
            ref var row = ref _workspace.Rows[index];
            row = new Row(opening, current, radius, reach);
            // The fixed upper spacing is a lower bound on the largest row spacing.
            if (largestObstacle > 0)
            {
                var upper = new Offset(current.SecondUpper) - new Offset(current.FirstUpper);
                row.EligibleThick = ChainThickSheetClearance.GapEligible(0.25 * upper.LengthSquared, radius, largestObstacle);
            }
            if (useCandidates)
            {
                var indices = _workspace.Indices(index);
                for (int obstacle = 0; obstacle < obstacles.Length; obstacle++)
                    if (particles.PlacementCull.ReachesBand(obstacle, reach))
                        indices[row.CandidateCount++] = obstacle;
            }
        }

        bool skipNoOps = ChainContactOptimizations.Use(ChainContactOptimization.CleanBands);
        bool useTolerance = ChainContactOptimizations.Use(ChainContactOptimization.PassTolerance);
        Candidate temporary = default;
        var counts = ChainContactCounts.Active;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool moved = false;
            for (int index = 0; index < bands.Length; index++)
            {
                var band = bands[index];
                ref var row = ref _workspace.Rows[index];
                row.Refresh(band.ReadCorners(particles.Positions));
                int revision = row.Revision;
                if (skipNoOps && row.LastNoOpRevision == revision)
                    continue;

                if (counts is not null)
                {
                    row.Visits++;
                    ChainContactCounts.Record(ChainContactQuery.BandVisit);
                }
                bool bandMoved = false;
                float radius = useCandidates ? row.Radius : Radius(band, settings);
                var indices = useCandidates ? _workspace.Indices(index)[..row.CandidateCount] : default;
                int count = useCandidates ? indices.Length : obstacles.Length;
                for (int candidate = 0; candidate < count; candidate++)
                {
                    int obstacle = useCandidates ? indices[candidate] : candidate;
                    if (!useCandidates && !particles.PlacementCull.ReachesBand(obstacle, row.Reach))
                        continue;

                    row.Refresh(band.ReadCorners(particles.Positions));
                    ref var entry = ref _workspace.ForObstacle(index, obstacle, ref temporary);
                    if (row.EligibleThick && SkipThick(particles, obstacle, obstacles[obstacle].Radius, ref row, ref entry))
                        continue;

                    double thickness = (double)radius + obstacles[obstacle].Radius;
                    bandMoved |= ProjectTriangle(particles, band, false, placed, obstacle, thickness, ref row, ref entry, useTolerance);
                    row.Refresh(band.ReadCorners(particles.Positions));
                    bandMoved |= ProjectTriangle(particles, band, true, placed, obstacle, thickness, ref row, ref entry, useTolerance);
                }

                row.LastNoOpRevision = bandMoved ? 0 : revision;
                moved |= bandMoved;
            }

            if (!moved)
                break;
        }

        if (counts is not null)
            for (int index = 0; index < bands.Length; index++)
                counts.RecordBandPasses(_workspace.Rows[index].Visits);
    }

    private bool CanReuseCandidates =>
        _workspace.CanStoreCandidates && ChainContactOptimizations.Use(ChainContactOptimization.BandCandidates);

    private static bool ProjectTriangle(ChainParticles particles, in Band band, bool secondTriangle,
        ChainConstraints.JointStep[] placed, int obstacle, double thickness, ref Row row, ref Candidate entry,
        bool useTolerance)
    {
        if (!TryCurrent(particles, ref row, ref entry, obstacle, secondTriangle, velocity: false, out var sample))
            return false;
        var hit = sample.Contact;
        var faceNormal = sample.Normal;
        double squared = hit.Residual.LengthSquared;
        double radiusSquared = thickness * thickness;
        double positionMargin = Math.Min(MaximumPositionMargin, 0.0001 * thickness);
        double sideTolerance = Math.Min(SideTolerance, positionMargin);
        var opening = ReadOpening(particles, row, ref entry, obstacle, secondTriangle);
        var openingNormal = opening.Normal;
        double openingSide = opening.Side;
        double currentSide = sample.Side;
        // A closest point can move across a triangle edge while the capsule remains
        // on the same side of the sheet. Only the normal component distinguishes sides.
        bool crossed = Math.Abs(openingSide) > sideTolerance && openingSide * currentSide < 0 &&
            ChainOpeningContact.Crossed(opening.Contact.Residual, hit.Residual, radiusSquared);
        if (squared >= radiusSquared && !crossed)
            return false;

        // A reset may seed a sphere inside the sheet's plane. Coplanar faces
        // share a deterministic side, including faces that meet at a junction.
        double firstChainWeight = secondTriangle ? hit.Weights.X : 1 - hit.Weights.Z;
        bool spansChains = firstChainWeight is > 0 and < 1;
        bool coplanar = spansChains && Math.Abs(openingSide) <= sideTolerance && Math.Abs(currentSide) <= sideTolerance &&
            Math.Sqrt(opening.Contact.Residual.LengthSquared) < thickness - TouchTolerance;
        Offset normal;
        if (crossed || coplanar || squared == 0)
            normal = Facing(faceNormal, openingNormal, openingSide, sideTolerance);
        else
            normal = hit.Residual * (1 / Math.Sqrt(squared));
        if (normal.LengthSquared == 0)
            return false;

        double firstWeight = secondTriangle ? 0 : hit.Weights.Y;
        double secondWeight = secondTriangle ? hit.Weights.Y : hit.Weights.Z;
        var first = placed[band.FirstChain];
        var second = placed[band.SecondChain];
        double movableSquared = (first.Length > 0 ? firstWeight * firstWeight : 0) +
            (second.Length > 0 ? secondWeight * secondWeight : 0);
        if (movableSquared <= MinimumWeight)
            return false;

        double axial = Offset.Dot(hit.Residual, normal);
        double separation = Math.Sqrt(Math.Max(0, radiusSquared - (squared - axial * axial))) - axial;
        if (!crossed && !coplanar)
        {
            double fixedWeight = hit.Weights.X + (secondTriangle ? hit.Weights.Z : 0);
            if (fixedWeight > 0)
            {
                // Only the preceding depth can clear its residual.
                separation -= fixedWeight * ReadFixedDeficit(particles, band, secondTriangle, obstacle, thickness, row.Radius, ref entry);
            }
        }
        // Leave a small margin before applying the lever. Starting the correction at
        // zero there keeps a grazing point from switching on a full positional push.
        if (separation <= positionMargin)
            return false;

        // Returning to the sphere removes the radial part of the requested push.
        // Account for that lost travel with a bounded first-order correction.
        double leverage = NormalLeverage(particles.Positions, first, normal, firstWeight) +
            NormalLeverage(particles.Positions, second, normal, secondWeight);
        double correction = (separation - positionMargin) / Math.Max(leverage, 0.05 * movableSquared);
        bool moved = Move(particles.Positions, first, normal, correction * firstWeight, useTolerance);
        return Move(particles.Positions, second, normal, correction * secondWeight, useTolerance) | moved;
    }

    private static Offset Facing(Offset normal, Offset openingNormal, double openingSide, double tolerance)
    {
        if (normal.LengthSquared == 0)
            normal = openingNormal;
        if (Math.Abs(openingSide) > tolerance)
            return openingSide < 0 ? normal * -1 : normal;

        // The dominant component gives a common half-space to coplanar faces
        // even when a nonmanifold chain edge is incident to several trails.
        var reference = openingNormal.LengthSquared > 0 ? openingNormal : normal;
        double component = Math.Abs(reference.Z) >= Math.Max(Math.Abs(reference.X), Math.Abs(reference.Y))
            ? reference.Z
            : Math.Abs(reference.Y) >= Math.Abs(reference.X) ? reference.Y : reference.X;
        return component < 0 ? normal * -1 : normal;
    }

    private static double NormalLeverage(Vector3[] positions, in ChainConstraints.JointStep step, Offset normal, double weight)
    {
        if (weight == 0 || step.Length <= 0)
            return 0;

        var radial = new Offset(positions[step.Particle + 1]) - new Offset(step.Start);
        var tangent = Offset.Cross(radial, normal);
        double squared = radial.LengthSquared;
        return squared == 0 ? 0 : weight * weight * tangent.LengthSquared / squared;
    }

    private static bool Move(Vector3[] positions, in ChainConstraints.JointStep step, Offset normal, double distance, bool useTolerance)
    {
        if (distance <= 0 || step.Length <= 0)
            return false;

        int child = step.Particle + 1;
        var initial = positions[child];
        var offset = new Offset(initial) - new Offset(step.Start) + normal * Math.Min(distance, step.Length);
        var direction = ChainMath.Direction(offset.ToVector3(), step.Direction);
        var next = step.Start + step.Length * direction;
        positions[child] = next;
        if (!useTolerance)
            return next != initial;

        var parent = new Offset(step.Start);
        return ChainContactPass.Moved(new Offset(initial) - parent, new Offset(next) - parent);
    }

    internal void StopVelocities(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ColliderCapsule> previous,
        ReadOnlySpan<ChainSettings> settings, float seconds)
    {
        if (seconds <= 0)
            return;

        var bands = _bandsByDepth[depth];
        bool useCandidates = CanReuseCandidates;
        Candidate temporary = default;
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            ref var row = ref _workspace.Rows[index];
            row.Refresh(band.ReadCorners(particles.Positions));
            double radius = (useCandidates ? row.Radius : Radius(band, settings)) + TouchTolerance;
            var indices = useCandidates ? _workspace.Indices(index)[..row.CandidateCount] : default;
            int count = useCandidates ? indices.Length : obstacles.Length;
            for (int candidate = 0; candidate < count; candidate++)
            {
                int obstacle = useCandidates ? indices[candidate] : candidate;
                if (!useCandidates && !particles.PlacementCull.ReachesBand(obstacle, row.Reach))
                    continue;

                ref var entry = ref _workspace.ForObstacle(index, obstacle, ref temporary);
                if (row.EligibleThick && SkipThick(particles, obstacle, obstacles[obstacle].Radius, ref row, ref entry))
                    continue;

                double thickness = radius + obstacles[obstacle].Radius;
                StopTriangle(particles, band, false, placed, obstacle,
                    obstacles[obstacle], previous[obstacle], thickness, seconds, ref row, ref entry);
                StopTriangle(particles, band, true, placed, obstacle,
                    obstacles[obstacle], previous[obstacle], thickness, seconds, ref row, ref entry);
            }
        }
    }

    private static void StopTriangle(ChainParticles particles, in Band band, bool secondTriangle,
        ChainConstraints.JointStep[] placed, int obstacle, in ColliderCapsule capsule, in ColliderCapsule previous,
        double thickness, float seconds, ref Row row, ref Candidate entry)
    {
        if (!TryCurrent(particles, ref row, ref entry, obstacle, secondTriangle, velocity: true, out var sample))
            return;
        var hit = sample.Contact;
        var faceNormal = sample.Normal;
        double squared = hit.Residual.LengthSquared;
        if (squared > thickness * thickness)
            return;

        Offset facing;
        if (squared == 0)
        {
            var opening = ReadOpening(particles, row, ref entry, obstacle, secondTriangle);
            double sideTolerance = Math.Min(SideTolerance, 0.0001 * (thickness - TouchTolerance));
            facing = Facing(faceNormal, opening.Normal, opening.Side, sideTolerance);
            if (facing.LengthSquared == 0)
                return;
        }
        else
        {
            facing = hit.Residual * (1 / Math.Sqrt(squared));
        }

        // The bend limit keeps its position. A piercing contact can still stop
        // tangent approach after that limit's local velocity constraint.
        var normal = facing.ToVector3();
        double firstWeight = secondTriangle ? 0 : hit.Weights.Y;
        double secondWeight = secondTriangle ? hit.Weights.Y : hit.Weights.Z;
        double firstUpperWeight = hit.Weights.X;
        double secondUpperWeight = secondTriangle ? hit.Weights.Z : 0;
        var firstSurface = SurfaceVelocity(capsule, previous, hit.Along, normal, particles.StepCarries[band.FirstChain], seconds);
        var secondSurface = SurfaceVelocity(capsule, previous, hit.Along, normal, particles.StepCarries[band.SecondChain], seconds);
        var velocities = particles.Velocities;
        ref var firstVelocity = ref velocities[band.FirstLower];
        ref var secondVelocity = ref velocities[band.SecondLower];
        double approach = firstWeight * Vector3.Dot(firstSurface - firstVelocity, normal) +
            secondWeight * Vector3.Dot(secondSurface - secondVelocity, normal) +
            firstUpperWeight * Vector3.Dot(firstSurface - velocities[band.FirstUpper], normal) +
            secondUpperWeight * Vector3.Dot(secondSurface - velocities[band.SecondUpper], normal);
        if (approach <= 0)
            return;

        var first = placed[band.FirstChain];
        var second = placed[band.SecondChain];
        var firstTangent = first.Length > 0 ? ChainMath.Tangent(normal, first.Direction) : Vector3.Zero;
        var secondTangent = second.Length > 0 ? ChainMath.Tangent(normal, second.Direction) : Vector3.Zero;
        double firstGradient = firstWeight * Vector3.Dot(normal, firstTangent);
        double secondGradient = secondWeight * Vector3.Dot(normal, secondTangent);
        double movableSquared = firstGradient * firstGradient + secondGradient * secondGradient;
        if (movableSquared <= MinimumWeight)
            return;

        // The fixed corners' part of the approach belongs to the preceding row.
        // Keep their weight in the denominator so a contact grazing that row cannot
        // abruptly stop a corner with almost no influence on it.
        double impulse = approach / (movableSquared +
            firstUpperWeight * firstUpperWeight + secondUpperWeight * secondUpperWeight);
        double strength = Math.Min(1, (thickness - Math.Sqrt(squared)) / TouchTolerance);
        RaiseVelocity(ref firstVelocity, firstSurface, firstTangent, impulse * firstGradient, strength);
        RaiseVelocity(ref secondVelocity, secondSurface, secondTangent, impulse * secondGradient, strength);
    }

    private static bool SkipThick(ChainParticles particles, int obstacle, float radius, ref Row row, ref Candidate entry)
    {
        if (entry.ThickRevision != row.Revision)
        {
            entry.ThickRevision = row.Revision;
            entry.SkipThick = ChainThickSheetClearance.CanSkip(row.Current, row.Opening, row.HalfGapSquared(), row.Radius, radius,
                particles.CapsuleAxes[obstacle], particles.PreviousCapsuleAxes[obstacle]);
        }
        if (entry.SkipThick)
            ChainContactCounts.Record(ChainContactQuery.ThickBandSkips);
        return entry.SkipThick;
    }

    private static bool TryCurrent(ChainParticles particles, ref Row row, ref Candidate entry, int obstacle,
        bool second, bool velocity, out Sample sample)
    {
        bool sphereCull = !ChainPlacementCull.DisableForTests &&
            ChainContactOptimizations.Use(ChainContactOptimization.TriangleCull);
        bool axisCull = ChainPlacementCull.AxisCullEnabled;
        if (sphereCull || axisCull)
        {
            var reach = Row.TriangleReach(ref row, second, velocity);
            if ((sphereCull && !particles.PlacementCull.ReachesTight(obstacle, reach)) ||
                (axisCull && !particles.PlacementCull.ReachesAxis(obstacle, reach, swept: !velocity)))
            {
                sample = default;
                return false;
            }
        }

        ref var cache = ref Candidate.Triangle(ref entry, second);
        var reuse = velocity ? ChainContactOptimization.VelocityTriangle : ChainContactOptimization.CurrentTriangle;
        int revision = row.TriangleRevision(second);
        if (ChainContactOptimizations.Use(reuse) && cache.Revision == revision)
        {
            sample = cache.Current;
            return true;
        }

        ChainContactCounts.Record(velocity ? ChainContactQuery.SheetVelocity : ChainContactQuery.SheetCurrent);
        var hit = row.Current.Triangle(second).Closest(particles.CapsuleAxes[obstacle], second, ref entry.CurrentDiagonal, out var normal);
        sample = new Sample(hit, normal, Offset.Dot(normal, hit.Residual));
        cache.Current = sample;
        cache.Revision = revision;
        return true;
    }

    private static Sample ReadOpening(ChainParticles particles, in Row row, ref Candidate entry, int obstacle, bool second)
    {
        ref var cache = ref Candidate.Triangle(ref entry, second);
        bool reuse = ChainContactOptimizations.Use(ChainContactOptimization.OpeningTriangle);
        if (reuse && cache.Opening is { } sample)
            return sample;

        ref readonly var openingAxis = ref particles.PreviousCapsuleAxes[obstacle];
        ref readonly var currentAxis = ref particles.CapsuleAxes[obstacle];
        bool sameAxis = openingAxis.Start.SameBits(currentAxis.Start) && openingAxis.End.SameBits(currentAxis.End);
        ref var diagonal = ref (sameAxis ? ref entry.CurrentDiagonal : ref entry.OpeningDiagonal);
        ChainContactCounts.Record(ChainContactQuery.SheetOpening);
        var triangle = reuse ? row.OpeningTriangle(second) : row.Opening.Triangle(second);
        var hit = triangle.Closest(openingAxis, second, ref diagonal, out var normal);
        var result = new Sample(hit, normal, Offset.Dot(normal, hit.Residual));
        if (reuse)
            cache.Opening = result;
        return result;
    }

    private static double ReadFixedDeficit(ChainParticles particles, in Band band, bool second, int obstacle,
        double thickness, float chainRadius, ref Candidate entry)
    {
        ref var cache = ref Candidate.Triangle(ref entry, second);
        bool reuse = ChainContactOptimizations.Use(ChainContactOptimization.FixedEdge);
        if (reuse && cache.FixedDeficit is { } deficit)
            return deficit;

        var upperStart = new Offset(particles.Positions[band.FirstUpper]);
        var upperEdge = second ? new Offset(particles.Positions[band.SecondUpper]) - upperStart : default;
        var segment = new ChainSegmentContact.Segment(upperStart, upperEdge);
        double value = 0;
        if (!ChainPlacementCull.AxisCullEnabled || particles.PlacementCull.ReachesAxis(obstacle,
            ChainPlacementCull.SegmentReach(segment, default, chainRadius + (float)TouchTolerance, includeOpening: false), swept: false))
        {
            ref readonly var axis = ref particles.CapsuleAxes[obstacle];
            ChainContactCounts.Record(ChainContactQuery.FixedEdge);
            var residual = !second && reuse ? axis.Residual(upperStart, default, out _) :
                axis.ClosestResidual(segment, out _, out _);
            value = Math.Max(0, thickness - Math.Sqrt(residual.LengthSquared));
        }
        if (reuse)
            cache.FixedDeficit = value;
        return value;
    }

    private static void RaiseVelocity(ref Vector3 velocity, Vector3 surface, Vector3 tangent, double amount, double strength)
    {
        double increase = Math.Min(amount, Vector3.Dot(surface - velocity, tangent));
        if (increase > 0 && double.IsFinite(increase))
            velocity += (float)(strength * increase) * tangent;
    }

    private static float Radius(in Band band, ReadOnlySpan<ChainSettings> settings) =>
        MathF.Max(settings[band.FirstChain].Radius, settings[band.SecondChain].Radius);
}
