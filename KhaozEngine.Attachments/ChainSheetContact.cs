using System;
using System.Collections.Generic;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal sealed class ChainSheetContact
{
    private const int MaxPasses = 8;
    private const double MinimumWeight = 1e-12;
    private const double SideTolerance = 2.5e-7;
    private const double MaximumPositionMargin = 2e-6;
    private readonly Band[][] _bandsByDepth;
    private readonly ChainPlacementCull.SweptSphere[] _reaches;

    // Counts complete triangle-axis queries, including the opening pose and velocity contacts.
    [ThreadStatic]
    internal static long? ClosestPointEvaluationsForTests;

    private readonly record struct Band(int FirstChain, int SecondChain, int FirstUpper, int SecondUpper,
        int FirstLower, int SecondLower)
    {
        internal ChainContactTriangle Triangle(Vector3[] positions, bool second) => second
            ? new(positions[FirstUpper], positions[SecondLower], positions[SecondUpper])
            : new(positions[FirstUpper], positions[FirstLower], positions[SecondLower]);
    }

    internal ChainSheetContact(ChainRig rig, IEnumerable<ChainWidthBounds.Pair> pairs, int depths)
    {
        var bands = new List<Band>?[depths];
        // Each width pair is a lower row. A tip pair closes from the last shared joint,
        // which need not be the actual parent of the longer chain's tip.
        foreach (var pair in pairs)
        {
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

        _reaches = new ChainPlacementCull.SweptSphere[maxBands];
    }

    internal void ProjectPositions(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ChainSettings> settings)
    {
        var bands = _bandsByDepth[depth];
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            var first = placed[band.FirstChain];
            var second = placed[band.SecondChain];
            _reaches[index] = ChainPlacementCull.BandReach(first.Start, first.Length, second.Start, second.Length,
                particles.Positions[band.FirstUpper], particles.Positions[band.SecondUpper], Radius(band, settings));
        }

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool moved = false;
            for (int index = 0; index < bands.Length; index++)
            {
                var band = bands[index];
                double radius = Radius(band, settings);
                for (int obstacle = 0; obstacle < obstacles.Length; obstacle++)
                {
                    if (!particles.PlacementCull.ReachesBand(obstacle, _reaches[index]))
                        continue;

                    double thickness = radius + obstacles[obstacle].Radius;
                    moved |= ProjectTriangle(particles, band, secondTriangle: false, placed, obstacle, thickness);
                    moved |= ProjectTriangle(particles, band, secondTriangle: true, placed, obstacle, thickness);
                }
            }

            if (!moved)
                break;
        }
    }

    private static bool ProjectTriangle(ChainParticles particles, in Band band, bool secondTriangle,
        ChainConstraints.JointStep[] placed, int obstacle, double thickness)
    {
        var triangle = band.Triangle(particles.Positions, secondTriangle);
        var hit = triangle.Closest(particles.CapsuleAxes[obstacle], out var faceNormal);
        double squared = hit.Residual.LengthSquared;
        double radiusSquared = thickness * thickness;
        double positionMargin = Math.Min(MaximumPositionMargin, 0.0001 * thickness);
        double sideTolerance = Math.Min(SideTolerance, positionMargin);
        var opening = band.Triangle(particles.OpeningPositions, secondTriangle)
            .Closest(particles.PreviousCapsuleAxes[obstacle], out var openingNormal);
        double openingSide = Offset.Dot(openingNormal, opening.Residual);
        double currentSide = Offset.Dot(faceNormal, hit.Residual);
        // A closest point can move across a triangle edge while the capsule remains
        // on the same side of the sheet. Only the normal component distinguishes sides.
        bool crossed = Math.Abs(openingSide) > sideTolerance && openingSide * currentSide < 0 &&
            ChainOpeningContact.Crossed(opening.Residual, hit.Residual, radiusSquared);
        if (squared >= radiusSquared && !crossed)
            return false;

        // A reset may seed a sphere inside the sheet's plane. Until it has a side,
        // use the band winding to pick one for the whole embedded patch.
        double firstChainWeight = secondTriangle ? hit.Weights.X : 1 - hit.Weights.Z;
        bool spansChains = firstChainWeight is > 0 and < 1;
        bool coplanar = spansChains && Math.Abs(openingSide) <= sideTolerance && Math.Abs(currentSide) <= sideTolerance &&
            Math.Sqrt(opening.Residual.LengthSquared) < thickness - TouchTolerance;
        Offset normal;
        if (crossed || coplanar || squared == 0)
            normal = openingSide < -sideTolerance ? faceNormal * -1 : faceNormal;
        else
            normal = hit.Residual * (1 / Math.Sqrt(squared));
        if (normal.LengthSquared == 0)
            normal = openingNormal;
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
                var upperStart = new Offset(particles.Positions[band.FirstUpper]);
                var upperEdge = secondTriangle ? new Offset(particles.Positions[band.SecondUpper]) - upperStart : default;
                var fixedResidual = particles.CapsuleAxes[obstacle]
                    .ClosestResidual(new ChainSegmentContact.Segment(upperStart, upperEdge), out _, out _);
                // Only the preceding depth can clear its own residual. Carry that fraction
                // through the constraint so it cannot be divided by the moving weight.
                separation -= fixedWeight * Math.Max(0, thickness - Math.Sqrt(fixedResidual.LengthSquared));
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
        bool moved = Move(particles.Positions, first, normal, correction * firstWeight);
        return Move(particles.Positions, second, normal, correction * secondWeight) | moved;
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
        positions[child] = step.Start + step.Length * direction;
        return positions[child] != initial;
    }

    internal void StopVelocities(ChainParticles particles, int depth, ChainConstraints.JointStep[] placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ColliderCapsule> previous,
        ReadOnlySpan<ChainSettings> settings, float seconds)
    {
        if (seconds <= 0)
            return;

        var bands = _bandsByDepth[depth];
        for (int index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            double radius = Radius(band, settings) + TouchTolerance;
            for (int obstacle = 0; obstacle < obstacles.Length; obstacle++)
            {
                if (!particles.PlacementCull.ReachesBand(obstacle, _reaches[index]))
                    continue;

                double thickness = radius + obstacles[obstacle].Radius;
                StopTriangle(particles, band, secondTriangle: false, placed, obstacle,
                    obstacles[obstacle], previous[obstacle], thickness, seconds);
                StopTriangle(particles, band, secondTriangle: true, placed, obstacle,
                    obstacles[obstacle], previous[obstacle], thickness, seconds);
            }
        }
    }

    private static void StopTriangle(ChainParticles particles, in Band band, bool secondTriangle,
        ChainConstraints.JointStep[] placed, int obstacle, in ColliderCapsule capsule, in ColliderCapsule previous,
        double thickness, float seconds)
    {
        var hit = band.Triangle(particles.Positions, secondTriangle).Closest(particles.CapsuleAxes[obstacle], out _);
        double squared = hit.Residual.LengthSquared;
        if (squared == 0 || squared > thickness * thickness)
            return;

        var normal = (hit.Residual * (1 / Math.Sqrt(squared))).ToVector3();
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

    private static void RaiseVelocity(ref Vector3 velocity, Vector3 surface, Vector3 tangent, double amount, double strength)
    {
        double increase = Math.Min(amount, Vector3.Dot(surface - velocity, tangent));
        if (increase > 0 && double.IsFinite(increase))
            velocity += (float)(strength * increase) * tangent;
    }

    private static float Radius(in Band band, ReadOnlySpan<ChainSettings> settings) =>
        MathF.Max(settings[band.FirstChain].Radius, settings[band.SecondChain].Radius);
}
