using System;
using System.Collections.Generic;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Attachments.ChainSheetWorkspace;

namespace KhaozEngine.Attachments;

internal sealed class ChainSheetContact
{
    internal const int MaxPasses = 8;

    [ThreadStatic]
    internal static bool DisabledForTests;
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

        internal Corners ReadPrediction(ChainSweepPrediction prediction, in Corners current) => new(
            prediction.Position(FirstChain, FirstUpper, current.FirstUpper),
            prediction.Position(SecondChain, SecondUpper, current.SecondUpper),
            prediction.Position(FirstChain, FirstLower, current.FirstLower),
            prediction.Position(SecondChain, SecondLower, current.SecondLower));
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

    internal void BeginRow(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ChainSettings> settings)
    {
        var bands = _bandsByDepth[depth];
        _workspace.BeginDepth();
        bool useCandidates = _workspace.CanStoreCandidates;
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            var first = placed[band.FirstChain];
            var second = placed[band.SecondChain];
            var opening = band.ReadCorners(particles.OpeningPositions);
            var current = band.ReadCorners(particles.Positions);
            var prediction = particles.SweepPrediction is { } sweep ? band.ReadPrediction(sweep, current) : current;
            float radius = Radius(band, settings);
            var reach = ChainPlacementCull.BandReach(first.Start, first.Length, second.Start, second.Length,
                current.FirstUpper, current.SecondUpper, radius, opening.FirstUpper, opening.SecondUpper,
                opening.FirstLower, opening.SecondLower,
                (prediction.FirstUpper, prediction.SecondUpper, prediction.FirstLower, prediction.SecondLower));
            ref var row = ref _workspace.Rows[index];
            row = new Row(opening, prediction, current, radius, reach);
            if (useCandidates)
            {
                var indices = _workspace.Indices(index);
                for (int obstacle = 0; obstacle < obstacles.Length; obstacle++)
                    if (particles.PlacementCull.ReachesBand(obstacle, reach))
                        indices[row.CandidateCount++] = obstacle;
            }
        }
    }

    internal bool ProjectPass(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles)
    {
        var bands = _bandsByDepth[depth];
        bool stored = _workspace.CanStoreCandidates;
        Candidate temporary = default;
        bool moved = false;
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            ref var row = ref _workspace.Rows[index];
            row.Refresh(band.ReadCorners(particles.Positions));
            int revision = row.Revision;
            if (row.LastNoOpRevision == revision)
                continue;

            var indices = stored ? _workspace.Indices(index)[..row.CandidateCount] : default;
            int count = stored ? indices.Length : obstacles.Length;
            bool bandMoved = false;
            for (int candidate = 0; candidate < count; candidate++)
            {
                int obstacle = stored ? indices[candidate] : candidate;
                if (!stored && !particles.PlacementCull.ReachesBand(obstacle, row.Reach))
                    continue;

                row.Refresh(band.ReadCorners(particles.Positions));
                ref var entry = ref _workspace.ForObstacle(index, obstacle, ref temporary);
                double thickness = (double)row.Radius + obstacles[obstacle].Radius;
                bandMoved |= ProjectTriangle(particles, band, false, placed, obstacle, thickness, ref row, ref entry);
                row.Refresh(band.ReadCorners(particles.Positions));
                bandMoved |= ProjectTriangle(particles, band, true, placed, obstacle, thickness, ref row, ref entry);
            }

            row.LastNoOpRevision = bandMoved ? 0 : revision;
            moved |= bandMoved;
        }

        return moved;
    }

    internal bool KeepsClearance(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles)
    {
        var bands = _bandsByDepth[depth];
        bool stored = _workspace.CanStoreCandidates;
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            var after = band.ReadCorners(particles.Positions);
            var first = placed[band.FirstChain];
            var second = placed[band.SecondChain];
            if (first.End == after.FirstLower && second.End == after.SecondLower)
                continue;

            ref readonly var row = ref _workspace.Rows[index];
            var before = new Corners(after.FirstUpper, after.SecondUpper, first.End, second.End);
            var left = new ChainSheetWidthClearance.Turn(first.Start, first.End, after.FirstLower, first.Length);
            var right = new ChainSheetWidthClearance.Turn(second.Start, second.End, after.SecondLower, second.Length);
            var indices = stored ? _workspace.Indices(index)[..row.CandidateCount] : default;
            int count = stored ? indices.Length : obstacles.Length;
            for (int candidate = 0; candidate < count; candidate++)
            {
                int obstacle = stored ? indices[candidate] : candidate;
                if (!stored && !particles.PlacementCull.ReachesBand(obstacle, row.Reach))
                    continue;

                ref readonly var axis = ref particles.CapsuleAxes[obstacle];
                double radius = (double)row.Radius + obstacles[obstacle].Radius;
                if (!ChainSheetWidthClearance.Preserves(before, axis, radius, left, right))
                    return false;
            }
        }

        return true;
    }

    private static bool ProjectTriangle(ChainParticles particles, in Band band, bool secondTriangle,
        ChainConstraints.JointStep[] placed, int obstacle, double thickness, ref Row row, ref Candidate entry)
    {
        if (!TryCurrent(particles, ref row, ref entry, obstacle, secondTriangle, velocity: false, out var sample))
            return false;
        var hit = sample.Contact;
        var faceNormal = sample.Normal;
        double squared = hit.Residual.LengthSquared;
        double radiusSquared = thickness * thickness;
        double positionMargin = Math.Min(MaximumPositionMargin, 0.0001 * thickness);
        double sideTolerance = Math.Min(SideTolerance, positionMargin);
        if (!ChainContactOptimizations.Use(ChainContactOptimization.SheetOpening))
            _ = ReadOpening(particles, row, ref entry, obstacle, secondTriangle);
        double currentSide = sample.Side;
        double sweptSide = SweptSide(particles, row, ref entry, obstacle, secondTriangle, thickness, sideTolerance);
        bool crossed = sweptSide != 0 && sweptSide * currentSide < 0;
        ref readonly var first = ref placed[band.FirstChain];
        ref readonly var second = ref placed[band.SecondChain];
        ref readonly var axis = ref particles.CapsuleAxes[obstacle];
        if (squared >= radiusSquared && (!crossed ||
            ChainPlacementCull.ClosingBandCannotReach(axis, thickness, first.Start, first.Length, second.Start, second.Length,
                row.Current.FirstUpper, row.Current.SecondUpper)))
            return false;

        // A reset may seed a sphere inside the sheet's plane. Coplanar faces
        // share a deterministic side, including faces that meet at a junction.
        double firstChainWeight = secondTriangle ? hit.Weights.X : 1 - hit.Weights.Z;
        bool spansChains = firstChainWeight is > 0 and < 1;
        bool mayBeCoplanar = spansChains && Math.Abs(currentSide) <= sideTolerance;
        var opening = crossed || mayBeCoplanar || squared == 0
            ? ReadOpening(particles, row, ref entry, obstacle, secondTriangle)
            : default;
        bool coplanar = mayBeCoplanar && Math.Abs(opening.Side) <= sideTolerance &&
            Math.Sqrt(opening.Contact.Residual.LengthSquared) < thickness - TouchTolerance;
        Offset normal;
        if (crossed || coplanar || squared == 0)
            normal = Facing(faceNormal, opening.Normal, sweptSide != 0 ? sweptSide : opening.Side, sideTolerance);
        else
            normal = hit.Residual * (1 / Math.Sqrt(squared));
        if (normal.LengthSquared == 0)
            return false;

        double firstWeight = secondTriangle ? 0 : hit.Weights.Y;
        double secondWeight = secondTriangle ? hit.Weights.Y : hit.Weights.Z;
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
        var counts = ChainContactCounts.Active;
        bool interiorTail = counts is not null && obstacle >= particles.SolidCapsuleCount && squared < radiusSquared &&
            hit.Weights is { X: > 0, Y: > 0, Z: > 0 } &&
            GuideClears(particles, band.FirstChain, axis, radiusSquared) &&
            GuideClears(particles, band.SecondChain, axis, radiusSquared);
        bool moved = Move(particles.Positions, first, normal, correction * firstWeight) |
            Move(particles.Positions, second, normal, correction * secondWeight);
        if (moved)
        {
            counts?.Record(ChainContactQuery.SheetCorrections);
            if (interiorTail)
                counts!.Record(ChainContactQuery.InteriorTailCorrections);
        }
        return moved;
    }

    private static bool GuideClears(ChainParticles particles, int chain, in Axis axis, double radiusSquared)
    {
        int root = particles.Rig.FirstJoint(chain) + chain;
        int tip = root + particles.Rig.Chains[chain].Joints.Count;
        var start = new Offset(particles.Positions[root]);
        for (int index = root + 1; index <= tip; index++)
        {
            var end = new Offset(particles.Positions[index]);
            var segment = new ChainSegmentContact.Segment(start, end - start);
            if (axis.ClosestResidual(segment, out _, out _).LengthSquared <= radiusSquared)
                return false;
            start = end;
        }

        return true;
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

    private static bool Move(Vector3[] positions, in ChainConstraints.JointStep step, Offset normal, double distance)
    {
        if (distance <= 0 || step.Length <= 0)
            return false;

        int child = step.Particle + 1;
        var initial = positions[child];
        var offset = new Offset(initial) - new Offset(step.Start) + normal * Math.Min(distance, step.Length);
        var direction = ChainMath.Direction(offset.ToVector3(), step.Direction);
        var next = step.Start + step.Length * direction;
        var parent = new Offset(step.Start);
        if (!ChainContactPass.Moved(new Offset(initial) - parent, new Offset(next) - parent))
            return false;

        positions[child] = next;
        return true;
    }

    internal void StopVelocities(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ColliderCapsule> previous,
        float seconds)
    {
        if (seconds <= 0)
            return;

        var bands = _bandsByDepth[depth];
        bool useCandidates = _workspace.CanStoreCandidates;
        Candidate temporary = default;
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            ref var row = ref _workspace.Rows[index];
            row.Refresh(band.ReadCorners(particles.Positions));
            double radius = row.Radius + TouchTolerance;
            var indices = useCandidates ? _workspace.Indices(index)[..row.CandidateCount] : default;
            int count = useCandidates ? indices.Length : obstacles.Length;
            for (int candidate = 0; candidate < count; candidate++)
            {
                int obstacle = useCandidates ? indices[candidate] : candidate;
                if (!useCandidates && !particles.PlacementCull.ReachesBand(obstacle, row.Reach))
                    continue;

                ref var entry = ref _workspace.ForObstacle(index, obstacle, ref temporary);

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
        double radius = thickness - TouchTolerance;
        double sideTolerance = Math.Min(SideTolerance, 0.0001 * radius);
        double sweptSide = SweptSide(particles, row, ref entry, obstacle, secondTriangle, radius, sideTolerance);
        if (squared == 0 || sweptSide * sample.Side < 0)
        {
            var opening = ReadOpening(particles, row, ref entry, obstacle, secondTriangle);
            facing = Facing(faceNormal, opening.Normal, sweptSide != 0 ? sweptSide : opening.Side, sideTolerance);
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

    private static bool TryCurrent(ChainParticles particles, ref Row row, ref Candidate entry, int obstacle,
        bool second, bool velocity, out Sample sample)
    {
        bool sphereCull = !ChainPlacementCull.DisableForTests;
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
        int revision = row.TriangleRevision(second);
        if (cache.Revision == revision)
        {
            sample = cache.Current;
            return true;
        }

        ChainContactCounts.Active?.Record(velocity ? ChainContactQuery.SheetVelocity : ChainContactQuery.SheetCurrent);
        var hit = row.Current.Triangle(second).Closest(particles.CapsuleAxes[obstacle], second, ref entry.CurrentDiagonal, out var normal);
        sample = new Sample(hit, normal, Offset.Dot(normal, hit.Residual));
        cache.Current = sample;
        cache.Revision = revision;
        return true;
    }

    private static double SweptSide(ChainParticles particles, in Row row, ref Candidate entry,
        int obstacle, bool second, double radius, double tolerance)
    {
        ref var state = ref Candidate.Triangle(ref entry, second);
        if (state.SweptSide is { } side)
            return side;

        var sweep = new ChainSheetSweep(row.OpeningTriangle(second), row.PredictionTriangle(second),
            particles.PreviousCapsuleAxes[obstacle], particles.CapsuleAxes[obstacle]);
        side = 0;
        if (sweep.CanCross(tolerance, out var openingNormal, out var lastNormal))
        {
            var opening = ReadOpening(particles, row, ref entry, obstacle, second);
            side = sweep.FindSide(opening.Contact, openingNormal, lastNormal,
                (double)row.Radius + particles.PreviousCapsuleRadii[obstacle], radius, tolerance);
        }
        state.SweptSide = side;
        return side;
    }

    private static Sample ReadOpening(ChainParticles particles, in Row row, ref Candidate entry, int obstacle, bool second)
    {
        ref var cache = ref Candidate.Triangle(ref entry, second);
        if (cache.Opening is { } sample)
            return sample;

        ref readonly var openingAxis = ref particles.PreviousCapsuleAxes[obstacle];
        ref readonly var currentAxis = ref particles.CapsuleAxes[obstacle];
        bool sameAxis = openingAxis.Start.SameBits(currentAxis.Start) && openingAxis.End.SameBits(currentAxis.End);
        ref var diagonal = ref (sameAxis ? ref entry.CurrentDiagonal : ref entry.OpeningDiagonal);
        ChainContactCounts.Active?.Record(ChainContactQuery.SheetOpening);
        var triangle = row.OpeningTriangle(second);
        var hit = triangle.Closest(openingAxis, second, ref diagonal, out var normal);
        var result = new Sample(hit, normal, Offset.Dot(normal, hit.Residual));
        cache.Opening = result;
        return result;
    }

    private static double ReadFixedDeficit(ChainParticles particles, in Band band, bool second, int obstacle,
        double thickness, float chainRadius, ref Candidate entry)
    {
        ref var cache = ref Candidate.Triangle(ref entry, second);
        if (cache.FixedDeficit is { } deficit)
            return deficit;

        var upperStart = new Offset(particles.Positions[band.FirstUpper]);
        var upperEdge = second ? new Offset(particles.Positions[band.SecondUpper]) - upperStart : default;
        var segment = new ChainSegmentContact.Segment(upperStart, upperEdge);
        double value = 0;
        if (!ChainPlacementCull.AxisCullEnabled || particles.PlacementCull.ReachesAxis(obstacle,
            ChainPlacementCull.SegmentReach(segment, default, chainRadius + (float)TouchTolerance, includeOpening: false), swept: false))
        {
            ref readonly var axis = ref particles.CapsuleAxes[obstacle];
            ChainContactCounts.Active?.Record(ChainContactQuery.FixedEdge);
            var residual = !second ? axis.Residual(upperStart, default, out _) :
                axis.ClosestResidual(segment, out _, out _);
            value = Math.Max(0, thickness - Math.Sqrt(residual.LengthSquared));
        }
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
