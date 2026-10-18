using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Single-Find evidence for the represented capsule seam and cylinder end tangency.</summary>
public sealed class ContactFollowupEndWorkTests(ITestOutputHelper output)
{
    [Fact]
    public void RepresentedEndTangencyCounterGate()
    {
        Assert.Equal(unchecked((int)0xbf333333), BitConverter.SingleToInt32Bits(-0.7f));
        foreach (bool capsule in new[] { true, false })
        {
            var left = Observe(capsule, MathF.BitIncrement(-0.7f));
            var critical = Observe(capsule, -0.7f);
            var right = Observe(capsule, MathF.BitDecrement(-0.7f));
            Write(capsule, MathF.BitIncrement(-0.7f), left);
            Write(capsule, -0.7f, critical);
            Write(capsule, MathF.BitDecrement(-0.7f), right);
            AssertComplete(left, capsule ? SupportStatus.Walkable : CylinderEndStatus(MathF.BitIncrement(-0.7f)));
            AssertComplete(critical, capsule ? SupportStatus.Walkable : CylinderEndStatus(-0.7f));
            AssertComplete(right, capsule ? SupportStatus.Walkable : CylinderEndStatus(MathF.BitDecrement(-0.7f)));
            if (capsule)
                Assert.True(critical.Work.Consumed <= 10L * Math.Min(left.Work.Consumed, right.Work.Consumed));
            else
            {
                var interior = Observe(false, -0.25f);
                AssertComplete(interior);
                Write(false, -0.25f, interior);
                long bound = 10L * interior.Work.Consumed;
                Assert.InRange(left.Work.Consumed, 1, bound);
                Assert.InRange(critical.Work.Consumed, 1, bound);
                Assert.InRange(right.Work.Consumed, 1, bound);
            }
        }
    }

    [Fact]
    public void CapsuleCrownCounterGate()
    {
        var left = Observe(true, MathF.BitIncrement(-0.7f));
        var critical = Observe(true, -0.7f);
        var right = Observe(true, MathF.BitDecrement(-0.7f));
        Write(true, MathF.BitIncrement(-0.7f), left);
        Write(true, -0.7f, critical);
        Write(true, MathF.BitDecrement(-0.7f), right);
        AssertComplete(left);
        AssertComplete(critical);
        AssertComplete(right);
        Assert.True(critical.Work.Consumed <= 10L * Math.Min(left.Work.Consumed, right.Work.Consumed));
    }

    [Theory]
    [InlineData(false, -1, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, -1, true)]
    [InlineData(true, 1, true)]
    [InlineData(false, -1, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, -1, false)]
    [InlineData(true, 1, false)]
    public void RepresentedCapsuleRoundedEndTraversalKeepsTheMillimetreBall(bool aboutX, int sign, bool along)
    {
        var left = Observe(true, MathF.BitIncrement(-0.7f));
        var right = Observe(true, MathF.BitDecrement(-0.7f));
        AssertComplete(left);
        AssertComplete(right);
        long maximumWork = 10L * Math.Max(left.Work.Consumed, right.Work.Consumed);
        var (scene, obstacle, across) = ContactFollowupScenes.Log(true, 0.3f, aboutX);
        using (scene)
        {
            int observations = 0;
            long highWater = 0;
            var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
            try
            {
                SupportQueryDiagnostics.SetCurvedWorkObserver(work =>
                {
                    observations++;
                    highWater = Math.Max(highWater, work.Consumed);
                    Assert.True(work.Failure == SupportCurvedWorkFailure.None, $"Find {observations}: {work}");
                    Assert.True(work.Consumed <= maximumWork, $"Find {observations}, bound {maximumWork}: {work}");
                });
                var tuning = ContactFollowupTraversalTests.Tuning;
                FootSupportQuery query = new(Vector2.Zero, 0.6f, ContactFollowupTraversalTests.FootRadius,
                    tuning.StepHeight, tuning.StepHeight, ContactFollowupTraversalTests.CosMaxSlope);
                SupportSample seat;
                using (var lease = scene.World.AcquireQueryReadLease())
                    seat = FootSupport.Find(null, scene.World, lease, query);
                Assert.Equal(SupportStatus.Walkable, seat.Status);
                Assert.Equal(obstacle, seat.Static);
                // Start on the crest, then cross the rounded-end seam or descend the walkable flank.
                Vector2 axis = aboutX ? Vector2.UnitY : Vector2.UnitX;
                Vector2 target = sign * (along ? 0.75f * axis : 0.3f * across);
                var expected = ContactFollowupScenes.LogSupport(true, 0.3f, aboutX, query with { Axis = target });
                Assert.Equal(SupportStatus.Walkable, expected.Status);
                var expectedTarget = new Vector3(target.X, (float)expected.Height, target.Y);
                var feet = new Vector3(0, seat.Height, 0);
                GroundStepResult result = default;
                bool stepping = false, arrived = false;
                int ticks = 0;
                for (; ticks < ContactFollowupTraversalTests.MaxTicks; ticks++)
                {
                    var move = ContactFollowupTraversalTests.Command(feet, target);
                    result = DryTick(scene.World, feet, move, stepping);
                    Assert.Equal(GroundFooting.Walkable, result.Footing);
                    Assert.Equal(obstacle, result.Support.Static);
                    Assert.Equal(0, result.Support.FeatureId);
                    Assert.InRange(result.Support.HeightError, 0, 0.0005f);
                    feet = result.Feet;
                    stepping = result.StepRise > 0;
                    if (Vector3.Distance(feet, expectedTarget) <= 0.001f && ContactFollowupTraversalTests.Seated(result))
                    { arrived = true; break; }
                }
                output.WriteLine($"traversal aboutX={aboutX} sign={sign} along={along} ticks={ticks + 1} " +
                    $"feet={feet} expected={expectedTarget} Finds={observations} maximumWork={highWater} bound={maximumWork}");
                Assert.True(observations > 0);
                Assert.True(arrived, $"Did not enter the 0.001 m Euclidean arrival ball in 240 ticks: {feet}, {expectedTarget}");
                Assert.False(result.Blocked);
                Assert.True(Vector3.Distance(feet, expectedTarget) <= 0.001f);
                Assert.Equal(expected.Static, result.Support.Static);
                Assert.Equal(expected.ElementId, result.Support.FeatureId);
                Assert.True((double)result.Support.Height - result.Support.HeightError <= expected.HeightLower);
                Assert.True((double)result.Support.Height + result.Support.HeightError >= expected.HeightUpper);
            }
            finally { SupportQueryDiagnostics.Exchange(prior); }
        }
    }

    static GroundStepResult DryTick(BepuPhysicsWorld world, Vector3 feet, Vector2 move, bool stepping)
    {
        using var lease = world.AcquireQueryReadLease();
        var memo = SupportRequestMemo.OpenDry(lease);
        Assert.NotNull(memo);
        try
        {
            var tuning = ContactFollowupTraversalTests.Tuning;
            var queries = new DryContactQueryContext(null, null, world, lease, tuning, null, memo);
            var result = GroundCore.Step(feet, move, ContactFollowupTraversalTests.Dt, tuning,
                ContactFollowupTraversalTests.Settings, queries, stepInProgress: stepping);
            if (result.Support.Static is not null && result.Support.Status == SupportStatus.Walkable)
                Assert.Same(lease, result.Support.Source.Result.Lease);
            return result;
        }
        finally { memo!.Close(); }
    }

    internal static (SupportSample Sample, SupportCurvedWorkObservation Work) Observe(bool capsule, float x)
    {
        var (scene, _, _) = ContactFollowupScenes.Log(capsule, radius: 0.3f, aboutX: false);
        using (scene)
        using (var lease = scene.World.AcquireQueryReadLease())
        {
            SupportCurvedWorkObservation work = default;
            int observations = 0;
            var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
            try
            {
                SupportQueryDiagnostics.SetCurvedWorkObserver(observation => { work = observation; observations++; });
                FootSupportQuery query = new(new Vector2(x, 0), 0.6f, 0.2f, 0.4f, 0.4f,
                    ContactFollowupTraversalTests.CosMaxSlope);
                SupportSample sample = FootSupport.Find(null, scene.World, lease, query);
                var counts = SupportQueryDiagnostics.Snapshot();
                Assert.Equal(1, counts.Find);
                Assert.Equal(2, counts.Proposals);
                Assert.Equal(1, observations);
                Assert.Equal(1048576, work.Limit);
                Assert.InRange(work.Consumed, 1, work.Limit);
                Assert.Equal(work.Consumed, work.New + work.Sign + work.Split + work.Compile +
                    work.RootCells + work.Refine + work.AdmissionCells);
                Assert.InRange(work.RootStorageHighWater, 0, 1024);
                Assert.InRange(work.AdmissionStorageHighWater, 0, 1024);
                return (sample, work);
            }
            finally { SupportQueryDiagnostics.Exchange(prior); }
        }
    }

    // Independent exact construction from the represented dyadics, not production predicates.
    // F(v) is concave on the upward barrel, so only its two endpoints can minimize disc distance.
    internal static SupportStatus CylinderEndStatus(float x)
    {
        R s = new(16777215, 1), n = s.Square() + R.One, h = new(1, 2);
        R r = R.From(0.3f), rho = R.From(0.2f), a = -R.From(x) - rho;
        R cy = new(10066331, 33554432), lower = R.From(0.6f) - R.From(0.4f);
        R upper = R.From(0.6f) + R.From(0.4f), cosine = R.From(ContactFollowupTraversalTests.CosMaxSlope);
        B length = B.Root(n);
        B d = -(B)(h * s) / length - (B)R.From(x);
        Assert.True(a > R.Zero && d.Lower > R.Zero);
        Assert.True(d.Lower.Square() + r.Square() > rho.Square()); // F(0) is outside.
        B capTangent = (B)cy + length * (B)h + (B)(s * R.From(x));
        Assert.True(capTangent.Upper < lower); // The upward cap cannot contribute in band.
        Assert.True(R.Zero < lower); // The floor cannot contribute either.
        R crestGap = n * a.Square() - (h * s - r).Square();
        if (crestGap <= R.Zero)
        {
            Assert.True(s.Square() > cosine.Square() * n);
            B crestHeight = (B)cy + (B)(h + r * s) / length;
            Assert.True(crestHeight.Lower >= lower && crestHeight.Upper <= upper);
            return SupportStatus.Walkable;
        }
        R solidGap = n * a.Square() - (h * s + r).Square();
        if (solidGap <= R.Zero)
            Assert.True(n * a.Square() < (h * s).Square()); // Only the steep cap meets this disc.
        return SupportStatus.None;
    }

    static void AssertComplete((SupportSample Sample, SupportCurvedWorkObservation Work) row,
        SupportStatus status = SupportStatus.Walkable)
    {
        Assert.Equal(status, row.Sample.Status);
        Assert.Equal(CapsuleFeatureStatus.Complete, row.Work.FootprintStatus);
        Assert.Equal(SupportCurvedWorkFailure.None, row.Work.Failure);
    }

    void Write(bool capsule, float x, (SupportSample Sample, SupportCurvedWorkObservation Work) row) =>
        output.WriteLine($"gate capsule={capsule} x={x:R} bits=0x{BitConverter.SingleToInt32Bits(x):x8} " +
            $"Find=1 Proposals=2 status={row.Sample.Status} {row.Work}");
}
