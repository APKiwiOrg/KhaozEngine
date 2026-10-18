using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed partial class CapturedStairExplicitSweepTests(ITestOutputHelper output) : IDisposable
{
    const float Dt = 1f / 30f;
    readonly SupportQueryDiagnostics.ThreadState prior =
        SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));

    public static IEnumerable<object[]> Installations()
    {
        foreach (bool compound in new[] { false, true })
            foreach (bool rotated in new[] { false, true })
                foreach (bool translated in new[] { false, true })
                {
                    yield return [compound, rotated, translated, false];
                    if (rotated) yield return [compound, rotated, translated, true];
                }
    }

    public static IEnumerable<object[]> Traversals() => Installations()
        .SelectMany(row => new[] { row.Concat(new object[] { false }).ToArray(), row.Concat(new object[] { true }).ToArray() });

    static CapturedStairSweepFixture Fixture(bool compound, bool rotated = false, bool translated = false) => new(compound,
        rotated ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) : Quaternion.Identity,
        translated ? new(16, 8, -16) : Vector3.Zero);

    [Fact]
    public void CapturedStairSourceAndFloatBitsAreStable()
    {
        using var capture = CapturedStairSweepFixture.ReadCapture();
        var root = capture.RootElement;
        Assert.Equal("APKiwiOrg/Grimhollow", root.GetProperty("sourceRepository").GetString());
        Assert.Equal("assets/models/kit/stairs.glb", root.GetProperty("sourcePath").GetString());
        Assert.Equal("f5ce2f2853608907e0bf6ae415969b1ef2fb70b8faf1adf146a60c4d67126af9",
            root.GetProperty("sourceSha256").GetString());
        var parts = root.GetProperty("parts");
        Assert.Equal(8, parts.GetArrayLength());
        float[] bottoms = [0, 0.22f, 0, 0.47f, 0, 0.72f, 0, 0.97f];
        float[] tops = [0.22f, 0.25f, 0.47f, 0.5f, 0.72f, 0.75f, 0.97f, 1];
        float[] starts = [-1, -1, -0.5f, -0.5f, -0f, -0f, 0.5f, 0.5f];
        float[] ends = [-0.5f, -0.5f, -0f, -0f, 0.5f, 0.5f, 1, 1];
        for (int i = 0; i < parts.GetArrayLength(); i++)
        {
            var part = parts[i];
            Assert.Equal(i, part.GetProperty("mesh").GetInt32());
            Assert.Equal(24, part.GetProperty("positionCount").GetInt32());
            Assert.Equal(new[] { 0f, 0, 0 }, part.GetProperty("nodeTranslation").EnumerateArray().Select(v => v.GetSingle()));
            Assert.Equal(new[] { 0f, 0, 0, 1 }, part.GetProperty("nodeRotation").EnumerateArray().Select(v => v.GetSingle()));
            Assert.Equal(new[] { 1f, 1, 1 }, part.GetProperty("nodeScale").EnumerateArray().Select(v => v.GetSingle()));
            Vector3 lo = new(-0.5f, bottoms[i], starts[i]), hi = new(0.5f, tops[i], ends[i]);
            Assert.Equal(Bits(lo), Bits(CapturedStairSweepFixture.Point(part.GetProperty("min"))));
            Assert.Equal(Bits(hi), Bits(CapturedStairSweepFixture.Point(part.GetProperty("max"))));
            var points = part.GetProperty("points").EnumerateArray().Select(CapturedStairSweepFixture.Point).ToArray();
            Assert.Equal(8, points.Length);
            var corners = new HashSet<(int, int, int)>();
            for (int corner = 0; corner < 8; corner++)
                corners.Add(Bits(new((corner & 1) == 0 ? lo.X : hi.X,
                    (corner & 2) == 0 ? lo.Y : hi.Y, (corner & 4) == 0 ? lo.Z : hi.Z)));
            Assert.True(corners.SetEquals(points.Select(Bits)));
        }
    }

    static (int, int, int) Bits(Vector3 point) =>
        (BitConverter.SingleToInt32Bits(point.X), BitConverter.SingleToInt32Bits(point.Y), BitConverter.SingleToInt32Bits(point.Z));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedStairPlayerScaleFirstLiftIsCertified(bool compound)
    {
        using var fixture = Fixture(compound);
        Assert.Equal(0.3f, fixture.Tuning.CapsuleRadius);
        Assert.Equal(0.75f, fixture.Tuning.CapsuleHalfHeight);
        Assert.Equal(0.4f, fixture.Tuning.StepHeight);
        Assert.Equal(0.15f, fixture.Tuning.CapsuleRadius * fixture.Tuning.FootRadiusFraction);
        Assert.Equal(0.001f, CapturedStairSweepFixture.PlacementOffset);
        Vector3 centre = new(0, 1.4510001f, -0.25f), delta = new(0, 0.4f, 0);
        var shell = new CapsuleShape(0.3f, 0.5f);
        using var lease = fixture.World.AcquireQueryReadLease();
        Span<CapsuleContact> contacts = new CapsuleContact[64];
        foreach (IPhysicsCapsuleGeometryQueries receiver in new IPhysicsCapsuleGeometryQueries[] { fixture.World, fixture.View })
        {
            var empty = receiver.QueryCapsuleContacts(shell, Pose.At(centre), 0, contacts, QueryFilter.StaticsOnly);
            Assert.True(empty.Complete);
            Assert.Equal(0, empty.Written);
            Assert.Equal(0, empty.RequiredCapacity);
            foreach (var result in new[] { receiver.SweepCapsuleCertified(shell, Pose.At(centre), delta, QueryFilter.StaticsOnly),
                         receiver.SweepCapsuleCertified(new CapsuleQueryDimensions(0.3f, 0.5f), Pose.At(centre), delta, QueryFilter.StaticsOnly) })
            {
                Assert.Equal(CapsuleSweepStatus.Clear, result.Status);
                Assert.Equal(0.4f, result.ClearThroughDistance);
                Assert.Null(result.ImpactDistance);
                Assert.InRange(result.CertifiedErrorMetres, 0, 0.001f);
            }
        }
        Assert.False(fixture.World.SweepCapsule(shell, Pose.At(centre), Vector3.UnitY, 0.4f, out _, QueryFilter.StaticsOnly));
    }

    [Theory]
    [MemberData(nameof(Installations))]
    public void CapturedStairAcquiredTickAdvancesTowardsPositiveZ(bool compound, bool rotated, bool translated, bool offTangency)
    {
        using var fixture = Fixture(compound, rotated, translated);
        fixture.RecordBindings = true;
        var state = fixture.StateAtFeet(fixture.Rotate(new(0, 0.5f, -0.25f + (offTangency ? 0.001f : 0))));
        Vector3 along = fixture.Rotate(Vector3.UnitZ);
        var capture = new TickCapture(fixture);
        MovementStepResult result = capture.Step(state, new(along.X, along.Z));
        if (rotated && !translated && !offTangency)
        {
            AssertSupportBoundary(fixture, state, result, capture);
            new CapturedStairEdgeOracle(fixture).AssertMapDisagreement(
                Assert.Single(capture.Footprints, f => f.Result.Status == CapsuleFeatureStatus.Unsupported).Request);
            Assert.Equal(new MovementSupportKey("world", "stairs"), result.State.Selection!.Value.Support);
            return;
        }
        if (offTangency) new CapturedStairEdgeOracle(fixture).AssertSeparated(capture.Footprints.Select(f => f.Request));
        AssertAccepted(fixture, state, result, capture.Reads);
        Assert.True(Vector3.Dot(result.State.State.Position - state.State.Position, along) > 0);
        Assert.True(capture.Counts.GroundSubsteps > 0);
        Assert.Equal(MovementAvailability.Known, fixture.LastAcquisitionStatus);
        Assert.NotEmpty(fixture.Bindings);
        Assert.Equal(new MovementSupportKey("world", "stairs"), result.State.Selection!.Value.Support);
        Assert.Throws<ObjectDisposedException>(fixture.LastLease!.AssertCurrent);
        output.WriteLine($"Compound {compound}, yaw {rotated}, origin {translated}. " +
            $"Advanced {result.State.State.Position - state.State.Position}, substeps {capture.Counts.GroundSubsteps}, " +
            $"support {result.State.Selection.Value.Support}.");
    }

    [Fact]
    public void CapturedStairArrivalOracleRejectsThePerAxisCube()
    {
        Assert.False(CertifiedSweepExactOracle.WithinArrivalBall(new(0.00075f), Vector3.Zero, 0.001f));
        Assert.True(CertifiedSweepExactOracle.WithinArrivalBall(new(0.001f, 0, 0), Vector3.Zero, 0.001f));
        Assert.False(CertifiedSweepExactOracle.WithinArrivalBall(new(MathF.BitIncrement(0.001f), 0, 0), Vector3.Zero, 0.001f));
    }

    [Theory]
    [MemberData(nameof(Traversals))]
    public void CapturedStairAscentAndDescentReachTheThreeDimensionalMillimetreBall(
        bool compound, bool rotated, bool translated, bool offTangency, bool descent)
    {
        using var fixture = Fixture(compound, rotated, translated);
        fixture.RecordBindings = true;
        float offset = offTangency ? (descent ? -0.001f : 0.001f) : 0;
        Vector3 low = fixture.Rotate(new(0, 0, -1.25f + offset)), high = fixture.Rotate(new(0, 1, 1.25f + offset));
        Vector3 target = descent ? low : high;
        var state = fixture.StateAtFeet(descent ? high : low);
        var capture = new TickCapture(fixture);
        var edges = offTangency ? new CapturedStairEdgeOracle(fixture) : null;
        var steps = new List<MovementStepResult>();
        bool sawStairs = false;
        int ticks = 0;
        for (; ticks < 240;)
        {
            Vector3 feet = fixture.SupportFeet(state);
            Vector2 command = new(target.X - feet.X, target.Z - feet.Z);
            command /= fixture.Tuning.WalkSpeed * Dt;
            if (command.LengthSquared() > 1) command = Vector2.Normalize(command);
            var result = capture.Step(state, command);
            ticks++;
            if (rotated && !translated && !offTangency && result.Outcome == MovementStepOutcome.Blocked)
            {
                Assert.All(steps, step => Assert.Equal(MovementStepOutcome.Advanced, step.Outcome));
                AssertSupportBoundary(fixture, state, result, capture);
                return;
            }
            edges?.AssertSeparated(capture.Footprints.Select(f => f.Request));
            AssertAccepted(fixture, state, result, capture.Reads);
            steps.Add(result);
            sawStairs |= fixture.Bindings.Any(b => b.Binding.Owner == new MovementSupportKey("world", "stairs"));
            state = result.State;
            if (CertifiedSweepExactOracle.WithinArrivalBall(fixture.SupportFeet(state), target, 0.001f)) break;
        }
        Assert.True(ticks <= 240);
        Assert.False(rotated && !translated && !offTangency, "The retained exact-edge row must exercise its support refusal.");
        Assert.True(sawStairs);
        Vector3 finalSupportFeet = fixture.SupportFeet(state);
        Assert.True(CertifiedSweepExactOracle.WithinArrivalBall(finalSupportFeet, target, 0.001f),
            $"After {ticks} ticks, feet {finalSupportFeet}, target {target}.");
        Assert.All(steps, step => Assert.Equal(MovementStepOutcome.Advanced, step.Outcome));
        Assert.Equal(new MovementSupportKey("world", descent ? "approach-pad" : "landing-pad"), state.Selection!.Value.Support);
        output.WriteLine($"Compound {compound}, yaw {rotated}, origin {translated}, descent {descent}. " +
            $"Arrived {finalSupportFeet} at {target} after {ticks} ticks.");
    }

    static void AssertAccepted(CapturedStairSweepFixture fixture, FramedMovementState before,
        MovementStepResult result, IReadOnlyList<SupportFindObservation> reads)
    {
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded || result.State.State.SupportGranted);
        AssertReadEvidence(fixture, reads);
        AssertPlacement(fixture, before, result);
    }

    static void AssertReadEvidence(CapturedStairSweepFixture fixture, IEnumerable<SupportFindObservation> reads)
    {
        Assert.NotEmpty(reads);
        foreach (var read in reads)
        {
            var expected = fixture.Expected(new(read.Axis, read.FeetY, read.Radius, read.ReachUp, read.ReachDown, read.CosMaxSlope), true);
            Assert.Equal(expected.Status, (SupportStatus)read.Status);
            if (expected.Status is SupportStatus.Walkable or SupportStatus.Steep)
                Assert.True(Math.Abs(expected.Height - read.Height) <= read.HeightError);
        }
        foreach (var (request, binding) in fixture.Bindings)
        {
            var face = Assert.Single(fixture.Scene.Faces, f => f.Static == request.Static && f.ElementId == request.FeatureId);
            var footprint = SupportFootprintTests.Request(request.Axis.X, request.Axis.Y, 0.15f,
                MathF.Cos(fixture.Tuning.MaxSlopeRadians), -30, 30);
            var member = SupportFootprintOracle.Polygon(face, footprint);
            Assert.NotNull(member);
            Assert.True(member.HasDiscMaximum);
            Assert.True(Math.Abs(Math.Min(member.Plane, member.Maximum) - request.Height) <= request.HeightError);
            Assert.Equal(new MovementSupportKey("world", request.Static == fixture.Approach ? "approach-pad"
                : request.Static == fixture.Landing ? "landing-pad" : "stairs"), binding.Owner);
            Assert.Equal(CapturedStairSweepFixture.QueryIdentity, binding.Identity);
        }
    }

    static void AssertPlacement(CapturedStairSweepFixture fixture, FramedMovementState before, MovementStepResult result)
    {
        Vector3 feet = fixture.SupportFeet(result.State) - result.State.Frame.PhysicsOrigin;
        var final = fixture.Expected(new(new(feet.X, feet.Z), feet.Y, 0.15f, 0.4f, 0.4f,
            MathF.Cos(fixture.Tuning.MaxSlopeRadians)), false);
        Assert.Equal(SupportStatus.Walkable, final.Status);
        double? roof = SupportCostRoofOracle.Limit(fixture.Scene, new(feet.X, feet.Z), 0.15f, final.Height, fixture.Tuning);
        Assert.Null(roof);
        Assert.True(feet.Y <= final.Height + CapturedStairSweepFixture.PlacementOffset);
        Assert.True(final.Height - feet.Y <= fixture.Tuning.StepHeight);
        Assert.True(result.State.State.Position.Y - before.State.Position.Y <= fixture.Tuning.MaxStepClimbSpeed * Dt);
    }

    sealed class TickCapture(CapturedStairSweepFixture fixture)
    {
        internal readonly List<SupportFindObservation> Reads = [];
        internal readonly List<(Pose Pose, Vector3 Delta, CapsuleSweepResult Result)> Sweeps = [];
        internal readonly List<(SupportFootprintRequest Request, SupportFootprintResult Result)> Footprints = [];
        internal SupportQueryCounts Counts;
        internal MovementStepResult Step(FramedMovementState state, Vector2 command)
        {
            Reads.Clear();
            Sweeps.Clear();
            Footprints.Clear();
            fixture.Bindings.Clear();
            SupportQueryDiagnostics.Reset();
            SupportQueryDiagnostics.SetFindObserver(Reads.Add);
            fixture.View.CertifiedSweepObserver = (pose, delta, result) => Sweeps.Add((pose, delta, result));
            fixture.View.FootprintObserver = (request, result) => Footprints.Add((request, result));
            try
            {
                var result = fixture.StepTowards(state, command, Dt);
                Counts = SupportQueryDiagnostics.Snapshot();
                return result;
            }
            finally
            {
                SupportQueryDiagnostics.SetFindObserver(null);
                fixture.View.CertifiedSweepObserver = null;
                fixture.View.FootprintObserver = null;
            }
        }
    }

    public void Dispose() => SupportQueryDiagnostics.Exchange(prior);
}
