using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public class FootSupportFootprintTests
{
    static FootSupportQuery Query => new(Vector2.Zero, 0, 0.2f, 0.125f, 0.4f, MathF.Cos(MathF.PI / 4));

    [Fact]
    public void CompleteFindUsesTwoProposalsAndOneVolume()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Flat("floor", -2, 2, -2, 2, 0);
        SupportQueryDiagnostics.Enable(true);
        try
        {
            SupportQueryDiagnostics.Reset();
            Assert.Equal(SupportStatus.Walkable, FootSupport.Find(null, null, scene.World, scene.Lease, Query).Status);
            var counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(2, counts.Proposals);
            Assert.Equal(1, counts.Volumes);
        }
        finally { SupportQueryDiagnostics.Enable(false); }
    }

    [Fact]
    public void RimFloorAtLowerReachIsFound()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Flat("rim", 0.195f, 0.201f, -0.01f, 0.01f, -0.4f);
        SupportSample sample = FootSupport.Find(null, null, scene.World, scene.Lease, Query);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(scene["rim"], sample.Static);
        Assert.True(Math.Abs(sample.Height - (double)-0.4f) <= sample.HeightError);
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(2.4f, true)]
    public void AdmissionStraddlesProbeStart(float lowerBy, bool admitted)
    {
        // y = 0.125 + 15*x initially, so the axis plane is 0.125 while the sliver lies above S.
        using var scene = new FootSupportScene(SceneVariant.Mesh).Mesh("sliver", FootSupportScene.Quad(
            new(0.19833334f, 3.1f - lowerBy, -0.001f), new(0.20833333f, 3.25f - lowerBy, -0.001f),
            new(0.19833334f, 3.1f - lowerBy, 0.001f), new(0.20833333f, 3.25f - lowerBy, 0.001f)));
        SupportSample sample = FootSupport.Find(null, null, scene.World, scene.Lease,
            Query with { ReachUp = 0.4f, ReachDown = 3f });
        Assert.Equal(admitted ? SupportStatus.Steep : SupportStatus.None, sample.Status);
        if (admitted)
        {
            double x0 = (double)0.19833334f, x1 = (double)0.20833333f;
            double y0 = (double)(3.1f - lowerBy), y1 = (double)(3.25f - lowerBy);
            double plane = (y0 * x1 - y1 * x0) / (x1 - x0);
            Assert.True(Math.Abs(sample.Height - plane) <= sample.HeightError);
        }
    }

    [Theory]
    [InlineData(0.199f, 0.201f, true)]
    [InlineData(0.15f, 0.16f, false)]
    public void FullRampMaximumDoesNotLoseValidAxisPlane(float left, float right, bool walkable)
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Mesh("ramp", FootSupportScene.Quad(
            new(left, 0.1f + left, -0.001f), new(right, 0.1f + right, -0.001f),
            new(left, 0.1f + left, 0.001f), new(right, 0.1f + right, 0.001f)));
        SupportSample sample = FootSupport.Find(null, null, scene.World, scene.Lease, Query);
        Assert.Equal(walkable ? SupportStatus.Walkable : SupportStatus.Refused, sample.Status);
        if (walkable)
        {
            double plane = ((double)(0.1f + left) * right - (double)(0.1f + right) * left) / ((double)right - left);
            Assert.True(Math.Abs(sample.Height - plane) <= sample.HeightError);
        }
    }

    [Fact]
    public void RingOnlyMemberNeverPublishesSupport()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Flat("ring", 0.20004f, 0.20006f, -0.001f, 0.001f, 0);
        Assert.Equal(SupportStatus.None, FootSupport.Find(null, null, scene.World, scene.Lease, Query).Status);
    }

    // y = -0.5 x over x [0.1, 0.3]. Its plane at the axis is 0, its disc maximum -0.05 at x 0.1, so the witness
    // decides the walkable contribution. A scope holding the axis but not the witness refuses.
    [Fact]
    public void RequiredWitnessOutsideScopeRefusesEvenWhenItsAxisFits()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Mesh("ramp", FootSupportScene.Quad(
            new(0.1f, -0.05f, -0.5f), new(0.3f, -0.15f, -0.5f), new(0.1f, -0.05f, 0.5f), new(0.3f, -0.15f, 0.5f)));
        Assert.Throws<NotSupportedException>(() => FootSupport.Find(null, scene.World, scene.Lease, Query,
            new SupportFootprintBounds(new(-2, -2, -2), new(0.05f, 2, 2))));
        SupportSample sample = FootSupport.Find(null, scene.World, scene.Lease, Query,
            new SupportFootprintBounds(new(-2, -2, -2), new(2, 2, 2)));
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.True(Math.Abs(sample.Height - (double)-0.05f) <= sample.HeightError, $"{sample}");
    }

    // The 30 degree roof ridge is at x 0, height 0.5. At x -0.1 the left plane, 0.5 - 0.1 tan 30, decides the left
    // member and caps the right member, whose own witness is the ridge at 0.5. A scope above that plane refuses.
    [Fact]
    public void RequiredPlaneCapOutsideScopeRefusesEvenWhenItsWitnessFits()
    {
        using var scene = FootSupportScenes.Ridge(SceneVariant.Mesh, 30);
        var query = Query with { Axis = new(-0.1f, 0), FeetY = 0.5f, ReachUp = 0.4f };
        Assert.Throws<NotSupportedException>(() => FootSupport.Find(null, scene.World, scene.Lease, query,
            new SupportFootprintBounds(new(-2, 0.45f, -2), new(2, 1, 2))));
        SupportSample sample = FootSupport.Find(null, scene.World, scene.Lease, query,
            new SupportFootprintBounds(new(-2, 0.4f, -2), new(2, 1, 2)));
        // The installed left plane through the ridge and the eave, as Ridge builds them.
        float angle = FootSupportScenes.Radians(30);
        double eaveX = 2 * MathF.Cos(angle), eaveY = 0.5f - 2 * MathF.Sin(angle);
        double plane = 0.5 - (double)0.1f * (0.5 - eaveY) / eaveX;
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.True(Math.Abs(sample.Height - plane) <= sample.HeightError, $"{sample}");
    }

    // y = 0.075 - 0.5 x over x [0.15, 0.3] falls away from the axis. Its plane at the axis is 0.075, above the
    // scope, while its disc maximum 0 at x 0.15 decides the contribution, so the plane is not required.
    [Fact]
    public void PlaneProvedAboveTheDecidingWitnessIsNotRequired()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Mesh("edge", FootSupportScene.Quad(
            new(0.15f, 0, -0.5f), new(0.3f, -0.075f, -0.5f), new(0.15f, 0, 0.5f), new(0.3f, -0.075f, 0.5f)));
        SupportSample sample = FootSupport.Find(null, scene.World, scene.Lease, Query,
            new SupportFootprintBounds(new(-2, -2, -2), new(2, 0.05f, 2)));
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(scene["edge"], sample.Static);
        Assert.True(Math.Abs(sample.Height) <= sample.HeightError, $"{sample}");
    }

    [Fact]
    public void ForeignLeaseCannotReachASweepMonitorOrLeaseCallback()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh);
        var lease = new ForeignLease();
        Assert.Throws<ArgumentException>(() => FootSupport.Find(null, null, scene.World, lease, Query));
        Assert.Equal(0, lease.Callbacks);
    }

    sealed class ForeignLease : IPhysicsQueryLease
    {
        internal int Callbacks;
        public IPhysicsWorld SourceWorld { get { Callbacks++; throw new InvalidOperationException(); } }
        public Vector3 Origin { get { Callbacks++; throw new InvalidOperationException(); } }
        public long GeometryGeneration { get { Callbacks++; throw new InvalidOperationException(); } }
        public void AssertCurrent() { Callbacks++; throw new InvalidOperationException(); }
        public void Dispose() { }
    }

    [Fact]
    public void TooSmallFootprintRejectsBeforeEveryPath()
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh);
        foreach (float radius in new[] { 0.005f, MathF.BitDecrement(0.01f), 0.01f, MathF.BitIncrement(0.01f) })
        {
            int samples = 0;
            float Height(float x, float z) { samples++; return 0; }
            if (radius < 0.01f)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => FootSupport.Find(Height, null, null, null,
                    Query with { FootRadius = radius }));
                Assert.Throws<ArgumentOutOfRangeException>(() => FootSupport.Find(null, null, scene.World, scene.Lease,
                    Query with { FootRadius = radius }));
                Assert.Equal(0, samples);
            }
            else
            {
                Assert.Equal(SupportStatus.Walkable, FootSupport.Find(Height, null, null, null,
                    Query with { FootRadius = radius }).Status);
                Assert.Equal(1, samples);
                Assert.Equal(SupportStatus.None, FootSupport.Find(null, null, scene.World, scene.Lease,
                    Query with { FootRadius = radius }).Status);
            }
            MoveTuning tuning = MoveTuning.Default with { CapsuleRadius = radius * 2 };
            if (radius < 0.01f)
                Assert.ThrowsAny<ArgumentException>(() => ContactShell.Validate(tuning));
            else ContactShell.Validate(tuning);
        }
        MoveTuning small = MoveTuning.Default with { CapsuleRadius = 0.01f };
        Assert.ThrowsAny<ArgumentException>(() => GroundCore.Step(Vector3.Zero, Vector2.Zero, 1f / 30,
            small, new(), DryQueries.For((x, z) => 0, null, null, null, small)));
        MoveTuning valid = small with { CapsuleRadius = 0.02f };
        Assert.Equal(GroundFooting.Walkable, GroundCore.Step(Vector3.Zero, Vector2.Zero, 1f / 30,
            valid, new(), DryQueries.For((x, z) => 0, null, null, null, valid)).Footing);
    }
}
