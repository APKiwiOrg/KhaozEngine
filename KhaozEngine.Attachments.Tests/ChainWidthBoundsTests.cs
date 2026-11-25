using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainWidthBoundsTests(ITestOutputHelper output)
{
    private const float Stretch = 0.12f;
    // Width sweeps share neighbours, so the allowed residual includes their remaining coupling.
    private const float WidthTolerance = 0.003f;
    private const int FramesPerSecond = 120;
    private const float StepSeconds = 1f / FramesPerSecond;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Running_cape_needs_the_width_bound_to_keep_linked_spacing(bool bounded)
    {
        // #1884: soft links alone let the moving panels leave the authored band.
        var rig = ChainSwingFixture.CreateRig(bounded ? Stretch : null, wideLimits: true);
        var simulation = new ChainSimulation(rig, 0);
        Assert.Equal(ChainSwingFixture.CapeChains - 1, rig.Links.Count);
        float largestViolation = 0;
        int worstFrame = 0, worstChain = 0, worstJoint = 0;
        for (int frame = 0; frame <= (ChainSwingFixture.StopTime + 1) * FramesPerSecond; frame++)
        {
            var (anchor, wearer) = ChainSwingFixture.Pose(frame / (float)FramesPerSecond);
            simulation.Step([anchor], [], wearer, frame == 0 ? 0 : StepSeconds);

            foreach (var link in rig.Links)
            {
                int count = rig.Chains[link.First].Joints.Count;
                for (int joint = 1; joint <= count; joint++)
                {
                    float rest = Vector3.Distance(Particle(rig, rig.RestFromAnchor, link.First, joint),
                        Particle(rig, rig.RestFromAnchor, link.Second, joint));
                    float spacing = Vector3.Distance(Particle(rig, simulation.JointsFromAnchor, link.First, joint),
                        Particle(rig, simulation.JointsFromAnchor, link.Second, joint));
                    float violation = MathF.Abs(spacing - rest) - rest * Stretch;
                    if (violation > largestViolation)
                    {
                        largestViolation = violation;
                        (worstFrame, worstChain, worstJoint) = (frame, link.First, joint);
                    }
                }
            }
        }

        output.WriteLine($"Largest cape width violation, bounded {bounded}: {largestViolation:F9} m at frame {worstFrame}, chain {worstChain}, joint {worstJoint}");
        if (bounded)
            Assert.InRange(largestViolation, 0, WidthTolerance);
        else
            Assert.True(largestViolation > WidthTolerance);
    }

    [Fact]
    public void Width_bound_leaves_capsule_clearance_and_bend_limits_in_charge()
    {
        var rig = ChainSwingFixture.CreateRig(Stretch);
        var simulation = new ChainSimulation(rig, 1);
        var center = new Vector3(0, -0.45f, -0.2f);
        const float radius = 0.09f;
        bool touched = false;
        bool exceededBendAtContact = false;
        for (int frame = 0; frame <= (ChainSwingFixture.StopTime + 1) * FramesPerSecond; frame++)
        {
            var (anchor, wearer) = ChainSwingFixture.Pose(frame / (float)FramesPerSecond);
            var worldCenter = Vector3.Transform(center, anchor);
            ColliderCapsule capsule = new(worldCenter, worldCenter, radius);
            simulation.Step([anchor], [capsule], wearer, frame == 0 ? 0 : StepSeconds);
            if (frame < 2)
                continue;

            for (int chain = 0; chain < ChainSwingFixture.CapeChains; chain++)
            {
                var definition = rig.Chains[chain];
                int first = rig.FirstJoint(chain);
                var parent = Matrix4x4.Identity;
                float clearance = radius + definition.Settings.Radius;
                float minimumCosine = MathF.Cos(definition.Settings.AngleLimit);
                for (int joint = 0; joint < definition.Joints.Count; joint++)
                {
                    int index = first + joint;
                    var start = simulation.JointsFromAnchor[index].Translation;
                    var end = Particle(rig, simulation.JointsFromAnchor, chain, joint + 1);
                    var segment = end - start;
                    float restLength = rig.SegmentOffset(index).Length();
                    Assert.InRange(MathF.Abs(segment.Length() - restLength), 0, 0.00002f);

                    float distance = ChainSegmentContactTests.DistanceToSegment(center, start, end);
                    Assert.True(distance >= clearance - 0.00002f, $"Frame {frame}, chain {chain}, joint {joint}, distance {distance:G9}, start {start}, end {end}.");
                    bool atContact = MathF.Abs(distance - clearance) <= 0.0002f;
                    touched |= atContact;
                    var unswung = rig.RestLocal(index) * parent;
                    var rest = Vector3.Normalize(Vector3.TransformNormal(rig.SegmentOffset(index), unswung));
                    if (Vector3.Dot(Vector3.Normalize(segment), rest) < minimumCosine - 0.00002f)
                    {
                        Assert.True(atContact);
                        exceededBendAtContact = true;
                    }
                    parent = simulation.JointsFromAnchor[index];
                }
            }
        }

        Assert.True(touched);
        Assert.True(exceededBendAtContact);
    }

    [Fact]
    public void A_width_bound_cannot_add_kinetic_energy_to_two_unforced_tips()
    {
        var (particles, bounds) = CreateTipPair(1);
        const float angle = 0.2f;
        float x = MathF.Sin(angle), y = MathF.Cos(angle);
        particles.Positions[1] = new Vector3(-x, -y, 0);
        particles.Positions[3] = particles.Positions[2] + new Vector3(x, -y, 0);
        particles.Velocities[1] = new Vector3(-y, x, 0);
        particles.Velocities[3] = new Vector3(y, x, 0);
        // Predict records the identity carry and gives the pair a sideways kick.
        PredictPair(particles, Vector3.Zero, Quaternion.Identity);

        float before = particles.Velocities[1].LengthSquared() + particles.Velocities[3].LengthSquared();
        bounds.ProjectPositions(particles);
        bounds.StopVelocities(particles, StepSeconds);
        var offset = particles.Positions[3] - particles.Positions[1];
        float after = particles.Velocities[1].LengthSquared() + particles.Velocities[3].LengthSquared();
        Assert.InRange(offset.Length(), 0.54999f, 0.55001f);
        Assert.InRange(Vector3.Dot(particles.Velocities[3] - particles.Velocities[1], Vector3.Normalize(offset)),
            -0.00001f, 0.00001f);
        Assert.InRange(after, 0, before + 0.00001f);
    }

    [Fact]
    public void Width_velocity_constraint_accounts_for_different_carried_motion()
    {
        var (particles, bounds) = CreateTipPair(0);
        var movement = new Vector3(-0.05f, 0, 0);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.2f);
        PredictPair(particles, movement, turn);
        bounds.ProjectPositions(particles);
        bounds.StopVelocities(particles, StepSeconds);

        var offset = particles.Positions[3] - particles.Positions[1];
        var firstCarry = movement + Vector3.Transform(-Vector3.UnitY, turn) + Vector3.UnitY;
        var relative = particles.Velocities[3] - (particles.Velocities[1] + firstCarry / StepSeconds);
        Assert.InRange(offset.Length(), 0.54999f, 0.55001f);
        Assert.InRange(Vector3.Dot(relative, Vector3.Normalize(offset)), -0.00001f, 0.00001f);
        Assert.InRange((particles.Velocities[1] + particles.Velocities[3]).Length(), 0, 0.00001f);
    }

    private static (ChainParticles Particles, ChainWidthBounds Bounds) CreateTipPair(float firstTrail, int colliderCount = 0, float secondLength = 1)
    {
        var settings = new ChainSettings(0, 0, 0, 1, MathF.PI, 0);
        ChainRigChain first = new(0, [new(Vector3.Zero, Quaternion.Identity, Vector3.One)], -Vector3.UnitY,
            settings with { Trail = firstTrail });
        var second = first with { Anchor = 1, Settings = settings, Tip = -secondLength * Vector3.UnitY };
        var particles = new ChainParticles(new ChainRig(2, [first, second], [new(0, 1) { Stretch = 0.1f }]), colliderCount);
        particles.SetRest([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0.5f, 0, 0)], reset: true);
        var bounds = new ChainWidthBounds([new(new(0, 1, 0), new(1, 3, 2), 0.1f)]);
        return (particles, bounds);
    }

    private static void PredictPair(ChainParticles particles, Vector3 movement, Quaternion turn)
    {
        particles.SnapshotOpening();
        for (int chain = 0; chain < 2; chain++)
        {
            var rig = particles.Rig;
            particles.Predict(chain, rig.Chains[chain].Settings, Vector3.Zero, StepSeconds,
                Vector3.Zero, movement, turn);
            var anchor = Matrix4x4.CreateTranslation(0.5f * chain, 0, 0);
            ChainConstraints.PlaceJoint(particles, chain, rig.FirstJoint(chain), anchor,
                0, -1, 0, StepSeconds, new ChainCapsuleCandidates([]));
        }
    }

    [Fact]
    public void Unequal_length_chains_bound_both_tips_when_the_longer_tip_is_placed()
    {
        // #1884: a short tip can be paired after its own depth has already finished.
        var settings = new ChainSettings(0, 0, 0, 1, MathF.PI, 0);
        ChainJointRest root = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        ChainJointRest child = root with { Translation = -0.5f * Vector3.UnitY };
        ChainRigChain shortChain = new(0, [root], -Vector3.UnitY, settings);
        ChainRigChain longChain = new(1, [root, child], -0.5f * Vector3.UnitY, settings);
        var bounded = new ChainSimulation(new ChainRig(2, [shortChain, longChain], [new(0, 1) { Stretch = 0.1f }]), 0);
        var loose = new ChainSimulation(new ChainRig(2, [shortChain, longChain], [new(0, 1)]), 0);
        var right = Matrix4x4.CreateTranslation(0.5f, 0, 0);
        var release = Matrix4x4.CreateRotationZ(-0.35f);
        var worlds = new Matrix4x4[3];

        float Observe(ChainSimulation simulation)
        {
            simulation.Step([release, right], [], Matrix4x4.Identity, 0);
            simulation.Step([Matrix4x4.Identity, right], [], Matrix4x4.Identity, StepSeconds);
            simulation.Step([Matrix4x4.Identity, right], [], Matrix4x4.Identity, StepSeconds);
            simulation.ComposeJointWorlds([Matrix4x4.Identity, right], worlds);
            var shortTip = Vector3.Transform(shortChain.Tip, worlds[0]);
            var longTip = Vector3.Transform(longChain.Tip, worlds[2]);
            Assert.InRange(Vector3.Distance(shortTip, worlds[0].Translation), 0.99999f, 1.00001f);
            Assert.InRange(Vector3.Distance(longTip, worlds[2].Translation), 0.49999f, 0.50001f);
            return Vector3.Distance(shortTip, longTip);
        }

        Assert.InRange(Observe(bounded), 0.4495f, 0.5505f);
        Assert.True(Observe(loose) > 0.5505f);
    }

    [Fact]
    public void Unbounded_chain_frames_are_bit_identical_beside_a_bounded_group()
    {
        // #1884: sharing a simulation with a width group must not change the soft-link path.
        var rig = ChainSwingFixture.CreateRig(wideLimits: true);
        int count = rig.Chains.Count;
        var bounded = rig.Chains[0];
        var mixedRig = new ChainRig(rig.AnchorCount,
            rig.Chains.Concat([bounded, bounded with { Joints = rig.Chains[1].Joints }]),
            rig.Links.Append(new ChainLink(count, count + 1) { Stretch = Stretch }));
        var standalone = new ChainSimulation(rig, 1);
        var mixed = new ChainSimulation(mixedRig, 1);
        var center = new Vector3(0, -0.45f, -0.2f);
        using var replay = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int frame = 0; frame <= (ChainSwingFixture.StopTime + 1) * FramesPerSecond; frame++)
        {
            var (anchor, wearer) = ChainSwingFixture.Pose(frame / (float)FramesPerSecond);
            var point = Vector3.Transform(center, anchor);
            ColliderCapsule capsule = new(point, point, 0.09f);
            float seconds = frame == 0 ? 0 : StepSeconds;
            standalone.Step([anchor], [capsule], wearer, seconds);
            mixed.Step([anchor], [capsule], wearer, seconds);
            var expected = MemoryMarshal.AsBytes(standalone.JointsFromAnchor);
            var actual = MemoryMarshal.AsBytes(mixed.JointsFromAnchor[..rig.JointCount]);
            Assert.True(expected.SequenceEqual(actual), $"Unbounded joint bits diverged at frame {frame}.");
            replay.AppendData(expected);
        }

        output.WriteLine($"Unbounded replay SHA256 {Convert.ToHexStringLower(replay.GetHashAndReset())}");
    }

    [Fact]
    public void A_short_tip_can_hold_two_bounds_at_different_depths()
    {
        // #1884: solving A to C later must not reopen the already solved A to B gap.
        var settings = new ChainSettings(0, 0, 0, 1, MathF.PI, 0);
        ChainJointRest root = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        ChainJointRest child = root with { Translation = -0.5f * Vector3.UnitY };
        ChainRigChain shortChain = new(0, [root], -Vector3.UnitY, settings);
        ChainRigChain longChain = new(2, [root, child], -0.5f * Vector3.UnitY, settings);
        var rig = new ChainRig(3, [shortChain, shortChain with { Anchor = 1 }, longChain],
            [new(0, 1) { Stretch = 0.1f }, new(0, 2) { Stretch = 0.1f }]);
        var particles = new ChainParticles(rig);
        Matrix4x4[] anchors = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(-0.5f, 0, 0),
            Matrix4x4.CreateTranslation(0.5f, 0, 0)];
        particles.SetRest(anchors, reset: true);
        for (int chain = 0; chain < rig.Chains.Count; chain++)
            particles.Predict(chain, settings, Vector3.Zero, StepSeconds, Vector3.Zero, Vector3.Zero, Quaternion.Identity);

        var tilt = new Vector3(MathF.Sin(0.35f), -MathF.Cos(0.35f), 0);
        particles.Positions[5] = particles.Positions[4] + 0.5f * tilt;
        particles.Positions[6] = particles.Positions[5] + 0.5f * tilt;
        new ChainLinks(rig, rig.Links, 0).FinishStep(particles, anchors, [], [], [settings, settings, settings], StepSeconds);

        Assert.InRange(Vector3.Distance(particles.Positions[1], particles.Positions[3]), 0.447f, 0.553f);
        Assert.InRange(Vector3.Distance(particles.Positions[1], particles.Positions[6]), 0.447f, 0.553f);
    }

    [Fact]
    public void Collinear_tips_can_turn_together_into_their_width_bound()
    {
        // #1884: both tangents of the pair axis vanish when the segments point directly apart.
        var (particles, bounds) = CreateTipPair(1);
        PredictPair(particles, Vector3.Zero, Quaternion.Identity);
        particles.Positions[1] = particles.Positions[0] - Vector3.UnitX;
        particles.Positions[3] = particles.Positions[2] + Vector3.UnitX;
        bounds.ProjectPositions(particles);

        Assert.InRange(Vector3.Distance(particles.Positions[1], particles.Positions[3]), 0.4495f, 0.5505f);
        Assert.InRange(Vector3.Distance(particles.Positions[0], particles.Positions[1]), 0.99999f, 1.00001f);
        Assert.InRange(Vector3.Distance(particles.Positions[2], particles.Positions[3]), 0.99999f, 1.00001f);
        Assert.True(Vector3.Dot(particles.Positions[1] - particles.Positions[0],
            particles.Positions[3] - particles.Positions[2]) > 0);
    }

    [Fact]
    public void An_impossible_width_pair_moves_to_its_reachable_edge_and_stays_there()
    {
        const float radius = 0.2f;
        const float bend = 0.16f;
        const float separated = 0.96f;
        var settings = new ChainSettings(0, 0, 0, 1, bend, 0);
        var joint = new ChainJointRest(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var chain = new ChainRigChain(0, [joint], -radius * Vector3.UnitY, settings);
        const float stretch = 0.08f;
        var rig = new ChainRig(2, [chain, chain with { Anchor = 1 }], [new(0, 1) { Stretch = stretch }]);
        var particles = new ChainParticles(rig);
        particles.SetRest([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0.18f, 0, 0)], reset: true);
        particles.Positions[2] = separated * Vector3.UnitX;
        particles.Positions[3] = particles.Positions[2] + chain.Tip;
        var bounds = new ChainWidthBounds([new(new(0, 1, 0), new(1, 3, 2), stretch)]);
        const float rounding = 0.000002f;

        bounds.ProjectPositions(particles);
        float nearest = separated - 2 * radius * MathF.Sin(bend);
        Assert.InRange(Vector3.Distance(particles.Positions[1], particles.Positions[3]), nearest - rounding, nearest + rounding);
        Assert.InRange(Vector3.Distance(particles.Positions[0], particles.Positions[1]), radius - rounding, radius + rounding);
        Assert.InRange(Vector3.Distance(particles.Positions[2], particles.Positions[3]), radius - rounding, radius + rounding);
        var left = particles.Positions[1];
        var right = particles.Positions[3];

        bounds.ProjectPositions(particles);
        Assert.InRange(Vector3.Distance(left, particles.Positions[1]), 0, rounding);
        Assert.InRange(Vector3.Distance(right, particles.Positions[3]), 0, rounding);
    }

    [Fact]
    public void A_width_sweep_keeps_a_supported_segment_clear_while_its_neighbour_moves()
    {
        var (particles, bounds) = CreateTipPair(1, colliderCount: 1);
        PredictPair(particles, Vector3.Zero, Quaternion.Identity);
        const float outward = 0.06f;
        float y = -MathF.Sqrt(1 - outward * outward);
        var left = new Vector3(-outward, y, 0);
        var right = particles.Positions[2] + new Vector3(outward, y, 0);
        particles.Positions[1] = left;
        particles.Positions[3] = right;
        const float radius = 0.15f;
        var center = left + radius * new Vector3(-y, -outward, 0);
        ColliderCapsule sphere = new(center, center, radius);
        particles.SetCapsuleGeometry(0, sphere, sphere);
        const float rounding = 0.000001f;
        Assert.InRange(ChainSegmentContactTests.DistanceToSegment(center, Vector3.Zero, left),
            radius - rounding, radius + rounding);

        bounds.ProjectPositions(particles, obstacles: [sphere]);

        Assert.True(ChainSegmentContactTests.DistanceToSegment(center, Vector3.Zero, particles.Positions[1])
            >= radius - rounding);
        Assert.True(Vector3.Distance(particles.Positions[1], particles.Positions[3]) < Vector3.Distance(left, right));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_bounded_tip_does_not_approach_a_capsule_holding_it(bool stretching)
    {
        // #1884: the other tip's escape impulse must not undo the contact stop.
        float secondLength = stretching ? 0.4f : 1f;
        var (particles, _) = CreateTipPair(1, colliderCount: 1, secondLength: secondLength);
        PredictPair(particles, Vector3.Zero, Quaternion.Identity);
        float x = stretching ? 0.1f : 0.06f;
        float y = -MathF.Sqrt(1 - x * x);
        const float radius = 0.15f;
        Vector3 center;
        if (stretching)
        {
            // The shorter tip leaves the capsule below the sheet while width
            // recovery brings the longer tip inward toward it.
            particles.Positions[1] = new Vector3(-x, y, 0);
            particles.Positions[3] = particles.Positions[2] + new Vector3(x, -MathF.Sqrt(secondLength * secondLength - x * x), 0);
            particles.Velocities[3] = Vector3.UnitX;
            center = particles.Positions[1] + radius * new Vector3(0.5f, -MathF.Sqrt(0.75f), 0);
        }
        else
        {
            particles.Positions[1] = new Vector3(x, y, 0);
            particles.Positions[3] = particles.Positions[2] + new Vector3(-x, y, 0);
            particles.Velocities[3] = new Vector3(y, x, 0);
            center = particles.Positions[1] - radius * Vector3.UnitX;
        }
        ColliderCapsule sphere = new(center, center, radius);
        particles.SetCapsuleGeometry(0, sphere, sphere);
        FinishTipPair(particles, [sphere]);

        var tip = particles.Positions[1];
        var root = particles.Positions[0];
        var contact = ChainSegmentContactTests.ClosestPointOnSegment(center, root, tip);
        Assert.InRange(Vector3.Distance(contact, center), radius - 0.00001f, radius + 0.00001f);
        var segment = Vector3.Normalize(tip - root);
        var normal = Vector3.Normalize(contact - center);
        var tangent = Vector3.Normalize(normal - Vector3.Dot(normal, segment) * segment);
        Assert.True(Vector3.Dot(particles.Velocities[1], tangent) >= -0.00001f,
            $"The held tip approaches the surface at {Vector3.Dot(particles.Velocities[1], tangent):F6} m/s.");
    }

    [Fact]
    public void A_bounded_pair_keeps_no_velocity_along_its_segments()
    {
        // #1884: width escape and segment length have different constraint axes.
        var (particles, _) = CreateTipPair(1);
        PredictPair(particles, Vector3.Zero, Quaternion.Identity);
        float x = MathF.Sin(0.2f), y = MathF.Cos(0.2f);
        particles.Positions[1] = new Vector3(-x, -y, 0);
        particles.Positions[3] = particles.Positions[2] + new Vector3(x, -y, 0);
        particles.Velocities[1] = new Vector3(-y, x, 0);
        particles.Velocities[3] = new Vector3(y, x, 0);
        FinishTipPair(particles, []);

        Assert.InRange(Vector3.Distance(particles.Positions[1], particles.Positions[3]), 0.4495f, 0.5505f);
        for (int root = 0; root <= 2; root += 2)
        {
            var segment = Vector3.Normalize(particles.Positions[root + 1] - particles.Positions[root]);
            var relative = particles.Velocities[root + 1] - particles.Velocities[root];
            Assert.InRange(Vector3.Dot(relative, segment), -0.00001f, 0.00001f);
        }
    }

    private static void FinishTipPair(ChainParticles particles, ReadOnlySpan<ColliderCapsule> colliders) =>
        new ChainLinks(particles.Rig, particles.Rig.Links, colliders.Length).FinishStep(particles, [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0.5f, 0, 0)],
            colliders, colliders, [particles.Rig.Chains[0].Settings, particles.Rig.Chains[1].Settings], StepSeconds);

    private static Vector3 Particle(ChainRig rig, ReadOnlySpan<Matrix4x4> frames, int chain, int joint)
    {
        var definition = rig.Chains[chain];
        int first = rig.FirstJoint(chain);
        return joint < definition.Joints.Count
            ? frames[first + joint].Translation
            : Vector3.Transform(definition.Tip, frames[first + joint - 1]);
    }
}
