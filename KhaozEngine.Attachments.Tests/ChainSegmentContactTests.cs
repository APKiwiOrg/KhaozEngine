using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainSegmentContactTests
{
    private const float StepSeconds = 1f / 120;
    private const float ClearanceTolerance = 0.0002f;

    [Fact]
    public void A_soft_chain_swinging_across_a_thin_capsule_keeps_clear_without_a_tip_jump()
    {
        // #1889: a capsule halfway along the first segment can be missed by both end points.
        var settings = Settings with { Stiffness = 2, Damping = 4, Trail = 0, Radius = 0.01f };
        var chain = Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var simulation = Create(chain, colliderCount: 1);
        var center = new Vector3(0, -0.5f, 0);
        var obstacle = TransverseCapsule(center, 0.045f);
        float radius = obstacle.Radius + settings.Radius;
        const float travel = 0.36f;
        const int movementSteps = 96;
        Matrix4x4 Anchor(int step) => Matrix4x4.CreateTranslation(travel * (step / (float)movementSteps - 0.5f), 0, 0);
        var opening = Anchor(0);
        simulation.Step([opening], [obstacle], opening, 0);
        Span<Matrix4x4> worlds = stackalloc Matrix4x4[2];
        Vector3 previousTip = opening.Translation - 2 * Vector3.UnitY;
        float minimum = float.PositiveInfinity, maximumMove = 0;

        for (int frame = 1; frame <= movementSteps + 1; frame++)
        {
            var anchor = Anchor(Math.Min(frame, movementSteps));
            simulation.Step([anchor], [obstacle], anchor, StepSeconds);
            // Compose on the previous anchor to read the previous solved sample at a grid boundary.
            simulation.ComposeJointWorlds([Anchor(frame - 1)], worlds);
            var tip = Vector3.Transform(chain.Tip, worlds[1]);
            minimum = MathF.Min(minimum, MathF.Min(
                DistanceToSegment(center, worlds[0].Translation, worlds[1].Translation),
                DistanceToSegment(center, worlds[1].Translation, tip)));
            maximumMove = MathF.Max(maximumMove, Vector3.Distance(tip, previousTip));
            previousTip = tip;
        }

        Assert.InRange(minimum, radius - ClearanceTolerance, radius + ClearanceTolerance);
        // Each of the two free particles can move with the anchor and swivel about its parent.
        Assert.InRange(maximumMove, 0, 8 * travel / movementSteps);
    }

    [Fact]
    public void A_soft_chain_dropped_over_a_capsule_settles_on_its_opening_side()
    {
        var settings = Settings with { Stiffness = 3, Damping = 5, Gravity = 0.5f, Radius = 0.01f };
        var chain = Chain(new(0.8f, 0.6f, 0), settings, Joint(Vector3.Zero), Joint(Vector3.UnitX));
        var simulation = Create(chain, colliderCount: 1);
        var center = new Vector3(0.55f, -0.23f, 0);
        var obstacle = TransverseCapsule(center, 0.05f);
        float radius = obstacle.Radius + settings.Radius;
        var anchor = Matrix4x4.Identity;
        simulation.Step([anchor], [obstacle], anchor, 0);
        float minimum = float.PositiveInfinity, settledTravel = 0;
        var previousJoint = Vector3.UnitX;
        var previousTip = previousJoint + chain.Tip;

        const int settleSteps = 8 * 120;
        for (int frame = 1; frame <= settleSteps; frame++)
        {
            simulation.Step([anchor], [obstacle], anchor, StepSeconds);
            var joint = simulation.JointsFromAnchor[1].Translation;
            var tip = Vector3.Transform(chain.Tip, simulation.JointsFromAnchor[1]);
            minimum = MathF.Min(minimum, MathF.Min(DistanceToSegment(center, Vector3.Zero, joint),
                DistanceToSegment(center, joint, tip)));
            if (frame > settleSteps - 120)
                settledTravel += Vector3.Distance(joint, previousJoint) + Vector3.Distance(tip, previousTip);
            previousJoint = joint;
            previousTip = tip;
        }

        Assert.InRange(minimum, radius - ClearanceTolerance, radius + ClearanceTolerance);
        Assert.InRange(DistanceToSegment(center, Vector3.Zero, previousJoint), radius - ClearanceTolerance, radius + ClearanceTolerance);
        var openingSide = Vector3.Normalize(new(-previousJoint.Y, previousJoint.X, 0));
        Assert.True(Vector3.Dot(previousJoint - center, openingSide) > 0);
        Assert.True(Vector3.Dot(previousTip - center, openingSide) > 0);
        Assert.InRange(settledTravel, 0, 0.001f);
    }

    [Fact]
    public void A_capsule_crossing_the_whole_segment_in_one_step_pushes_it_ahead()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        var simulation = Create(chain, colliderCount: 1);
        const float radius = 0.08f;
        var before = new Vector3(-0.3f, -0.5f, 0);
        var after = new Vector3(0.3f, -0.5f, 0);
        var anchor = Matrix4x4.Identity;
        simulation.Step([anchor], [TransverseCapsule(before, radius)], anchor, 0);
        simulation.Step([anchor], [TransverseCapsule(after, radius)], anchor, StepSeconds);
        simulation.Step([anchor], [TransverseCapsule(after, radius)], anchor, StepSeconds);

        var tip = Vector3.Transform(chain.Tip, simulation.JointsFromAnchor[0]);
        Assert.True(tip.X > after.X, $"The tip stayed behind the capsule at {tip}.");
        Assert.InRange(DistanceToSegment(after, Vector3.Zero, tip), radius - ClearanceTolerance, radius + ClearanceTolerance);
    }

    [Fact]
    public void A_clear_predicted_segment_does_not_cross_because_its_parent_was_projected()
    {
        var settings = Settings with { Gravity = 0 };
        var chain = Chain(-Vector3.UnitY, settings, Joint(Vector3.Zero), Joint(-Vector3.UnitY));
        var upstream = TransverseCapsule(new(0.03f, -0.5f, 0), 0.22f);
        var below = TransverseCapsule(new(-0.09f, -1.38f, 0), 0.06f);
        var (parent, end) = SolveSaved(chain, -2 * Vector3.UnitY, -2 * Vector3.UnitY, [upstream]);
        var center = (below.Start + below.End) / 2;
        Assert.True(DistanceToSegment(center, -Vector3.UnitY, -2 * Vector3.UnitY) > below.Radius);
        Assert.True(DistanceToSegment(center, parent, end) > below.Radius);
        Assert.True(parent.X < center.X && end.X > center.X);

        var (actualParent, actualEnd) = SolveSaved(chain, -2 * Vector3.UnitY, -2 * Vector3.UnitY, [upstream, below]);
        Assert.Equal(parent, actualParent);
        Assert.InRange(Vector3.Distance(end, actualEnd), 0, ClearanceTolerance);
    }

    [Fact]
    public void An_embedded_opening_uses_the_nearest_side_of_the_prediction()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        var center = -0.5f * Vector3.UnitY;
        const float radius = 0.1f;
        var opening = new Vector3(0.04f, -MathF.Sqrt(1 - 0.04f * 0.04f), 0);
        var predicted = opening with { X = -opening.X };
        Assert.True(DistanceToSegment(center, Vector3.Zero, opening) < radius);
        Assert.True(DistanceToSegment(center, Vector3.Zero, predicted) < radius);

        var (_, end) = SolveSaved(chain, opening, predicted, [TransverseCapsule(center, radius)]);
        Assert.True(end.X < predicted.X, $"The correction crossed the capsule to {end}.");
        Assert.InRange(DistanceToSegment(center, Vector3.Zero, end), radius - ClearanceTolerance, radius + ClearanceTolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Competing_solids_choose_the_nearest_clear_turn_from_the_saved_pose(bool reversed)
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        const float offset = 0.08f;
        const float depth = 0.45f;
        const float radius = 0.10f;
        var a = new Vector3(-offset, -depth, 0);
        var b = new Vector3(offset, -depth, 0);
        ColliderCapsule[] solids = reversed ? [new(b, b, radius), new(a, a, radius)] :
            [new(a, a, radius), new(b, b, radius)];
        var (_, end) = SolveSaved(chain, chain.Tip, chain.Tip, solids);

        // A turn perpendicular to the plane of the two centers gives both the
        // same tangent distance. Any in-plane component approaches one sphere.
        float sine = MathF.Sqrt(radius * radius - offset * offset) / depth;
        float nearestDown = MathF.Sqrt(1 - sine * sine);
        Assert.InRange(DistanceToSegment(a, Vector3.Zero, end), radius - ClearanceTolerance, radius + ClearanceTolerance);
        Assert.InRange(DistanceToSegment(b, Vector3.Zero, end), radius - ClearanceTolerance, radius + ClearanceTolerance);
        Assert.InRange(-end.Y, nearestDown - ClearanceTolerance, nearestDown + ClearanceTolerance);
        var (_, settled) = SolveSaved(chain, end, end, solids);
        Assert.InRange(Vector3.Distance(end, settled), 0, ClearanceTolerance);
    }

    [Fact]
    public void Unreachable_solid_clearance_keeps_the_smallest_residual_without_a_turn()
    {
        var chain = Chain(-Vector3.UnitY, Settings with { Gravity = 0 }, Joint(Vector3.Zero));
        var offset = 0.1f * Vector3.UnitX;
        const float radius = 1.2f;
        ColliderCapsule[] solids = [new(offset, offset, radius), new(-offset, -offset, radius)];
        // Every endpoint lies inside both solid spheres. The two distances
        // have the greatest shared minimum on the plane equidistant from both.
        float minimumResidual = radius - MathF.Sqrt(chain.Tip.LengthSquared() + offset.LengthSquared());
        var (_, end) = SolveSaved(chain, chain.Tip, chain.Tip, solids);

        float residual = MathF.Max(radius - Vector3.Distance(end, offset), radius - Vector3.Distance(end, -offset));
        Assert.InRange(residual, minimumResidual - ClearanceTolerance, minimumResidual + ClearanceTolerance);
        Assert.InRange(Vector3.Distance(end, chain.Tip), 0, ClearanceTolerance);
    }

    private static (Vector3 Parent, Vector3 End) SolveSaved(ChainRigChain chain, Vector3 opening,
        Vector3 predicted, ReadOnlySpan<ColliderCapsule> capsules)
    {
        var particles = new ChainParticles(new ChainRig(1, [chain], []), capsules.Length);
        particles.SetRest([Matrix4x4.Identity], reset: true);
        particles.Positions[^1] = opening;
        particles.SnapshotOpening();
        particles.Predict(0, chain.Settings, Vector3.Zero, StepSeconds, Vector3.Zero, Vector3.Zero, Quaternion.Identity);
        particles.Positions[^1] = predicted;
        for (int index = 0; index < capsules.Length; index++)
            particles.SetCapsuleGeometry(index, capsules[index], capsules[index]);

        ChainConstraints.FollowChain(particles, 0, Matrix4x4.Identity, capsules, capsules, chain.Settings, StepSeconds,
            pullTowardRest: true);
        return (particles.Positions[^2], particles.Positions[^1]);
    }

    [Fact]
    public void Corrected_world_endpoint_and_segment_clear_after_float_rounding()
    {
        var root = new Vector3(32769, 0, 0);
        var center = root + Vector3.UnitY;
        const float radius = 0.064f;
        const float startX = 0.0625f;
        var incoming = new Vector3(startX, MathF.Sqrt(1 - startX * startX), 0);
        var end = CorrectSegment(root, center, radius, incoming);
        var storedOffset = end - root;
        Assert.True(Vector3.Distance(storedOffset, center - root) >= radius, $"Stored endpoint {end} is inside the sphere.");
        Assert.True(DistanceToSegment(center - root, Vector3.Zero, storedOffset) >= radius, $"Stored segment to {end} crosses the sphere.");
    }

    [Fact]
    public void A_tiny_contact_near_the_parent_turns_only_to_the_tangent_cone()
    {
        const float radius = 1e-8f;
        var center = new Vector3(0, 1e-7f, 0);
        var end = CorrectSegment(Vector3.Zero, center, radius, Vector3.UnitY);
        float tangentAngle = MathF.Asin(radius / center.Y);
        float angle = MathF.Atan2(new Vector2(end.X, end.Z).Length(), end.Y);
        Assert.InRange(angle, tangentAngle - 0.000001f, tangentAngle + 0.000001f);
        Assert.True(DistanceToSegment(center, Vector3.Zero, end) >= radius);
    }

    private static Vector3 CorrectSegment(Vector3 root, Vector3 center, float radius, Vector3 incoming)
    {
        var settings = Settings with { Gravity = 0 };
        var definition = Chain(Vector3.UnitY, settings, Joint(Vector3.Zero));
        var particles = new ChainParticles(new ChainRig(1, [definition], []), colliderCount: 1);
        var anchor = Matrix4x4.CreateTranslation(root);
        ColliderCapsule sphere = new(center, center, radius);
        particles.SetRest([anchor], reset: true);
        particles.Positions[1] = root + incoming;
        particles.SnapshotOpening();
        particles.SetCapsuleGeometry(0, sphere, sphere);
        ChainConstraints.FollowChain(particles, 0, anchor, [sphere], [sphere], settings, StepSeconds, pullTowardRest: false);
        return particles.Positions[1];
    }

    internal static float DistanceToAxis(Vector3 start, Vector3 end, in ColliderCapsule capsule)
    {
        if (capsule.Start == capsule.End)
            return DistanceToSegment(capsule.Start, start, end);

        // A constrained minimum is at an endpoint unless the closest pair on
        // the two infinite lines lies inside both finite segments.
        float nearest = MathF.Min(DistanceToSegment(start, capsule.Start, capsule.End),
            MathF.Min(DistanceToSegment(end, capsule.Start, capsule.End),
                MathF.Min(DistanceToSegment(capsule.Start, start, end), DistanceToSegment(capsule.End, start, end))));
        var segment = end - start;
        var axis = capsule.End - capsule.Start;
        var normal = Vector3.Cross(segment, axis);
        float squared = normal.LengthSquared();
        if (squared <= 0)
            return nearest;

        var between = capsule.Start - start;
        float s = Vector3.Dot(Vector3.Cross(between, axis), normal) / squared;
        float t = Vector3.Dot(Vector3.Cross(between, segment), normal) / squared;
        if (s is >= 0 and <= 1 && t is >= 0 and <= 1)
            nearest = MathF.Min(nearest, Vector3.Distance(start + s * segment, capsule.Start + t * axis));
        return nearest;
    }

    internal static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end) =>
        Vector3.Distance(point, ClosestPointOnSegment(point, start, end));

    internal static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var segment = end - start;
        float squared = segment.LengthSquared();
        float along = squared == 0 ? 0 : Math.Clamp(Vector3.Dot(point - start, segment) / squared, 0, 1);
        return start + along * segment;
    }

    private static ColliderCapsule TransverseCapsule(Vector3 center, float radius) =>
        new(center - Vector3.UnitZ, center + Vector3.UnitZ, radius);
}
