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

    internal static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end) =>
        Vector3.Distance(point, ClosestPointOnSegment(point, start, end));

    internal static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var segment = end - start;
        float along = Math.Clamp(Vector3.Dot(point - start, segment) / segment.LengthSquared(), 0, 1);
        return start + along * segment;
    }

    private static ColliderCapsule TransverseCapsule(Vector3 center, float radius) =>
        new(center - Vector3.UnitZ, center + Vector3.UnitZ, radius);
}
