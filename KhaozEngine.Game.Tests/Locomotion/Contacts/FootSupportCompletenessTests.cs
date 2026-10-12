using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class FootSupportCompletenessTests(ITestOutputHelper output)
{
    [Fact]
    public void RelevantCapacityDistinguishesBelowBandAndInBandFans()
    {
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        SupportFootprintRequest request = SupportFootprintTests.Request(cosine: query.CosMaxSlope,
            lower: -(double)query.ReachDown, upper: query.ReachUp);
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[SupportFootprintResult.MaximumElements *
            SupportFootprintResult.JoinWordsFor(SupportFootprintResult.MaximumElements)];
        SupportFootprintMember untouched = default(SupportFootprintMember) with
        { ElementId = -99, PlanePoint = new(1, 2, 3) };
        foreach (float height in new[] { -0.4005f, 0f })
        {
            using FootSupportScene scene = FootSupportScenes.OverCapacityFan(new(0, height, 0));
            SupportSample released = FootSupport.Find(null, null, scene.World, scene.Lease, query);
            Array.Fill(members, untouched);
            Array.Fill(joins, ulong.MaxValue);
            SupportFootprintResult complete = scene.World.QuerySupportFootprint(scene.Lease, request, members,
                joins, QueryFilter.StaticsOnly);
            output.WriteLine($"Fan Y={height:R}, released={released.Status}, footprint={complete.Status}, " +
                $"required={complete.RequiredElements}, members={complete.Members}, witnesses={complete.Witnesses}, " +
                $"axis={complete.AxisVerdict}, foot={complete.FootVerdict}");

            Assert.Equal(height < -(double)query.ReachDown ? SupportStatus.None : SupportStatus.Refused,
                released.Status);
            if (height < -(double)query.ReachDown)
            {
                Assert.Equal(CapsuleFeatureStatus.Complete, complete.Status);
                Assert.Equal(0, complete.Members);
                Assert.Equal(0, complete.Witnesses);
                Assert.Equal(SupportProposalStatus.Contact, complete.AxisVerdict.Status);
                Assert.True(complete.AxisVerdict.LowestUpper < -(double)query.ReachDown);
                Assert.Equal(SupportProposalStatus.Contact, complete.FootVerdict.Status);
                Assert.True(complete.FootVerdict.LowestUpper < -(double)query.ReachDown);
                Assert.Same(scene.Lease, complete.Lease);
                Assert.All(members, member => Assert.Equal(untouched, member));
                Assert.All(joins, word => Assert.Equal(ulong.MaxValue, word));
                continue;
            }
            Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, complete.Status);
            Assert.Equal(257, complete.RequiredElements);
            Assert.Equal(0, complete.Members);
            Assert.Equal(0, complete.Witnesses);
            Assert.Equal(default, complete.AxisVerdict);
            Assert.Equal(default, complete.FootVerdict);
            Assert.Null(complete.QueryWorld);
            Assert.Null(complete.Lease);
            Assert.All(members, member => Assert.Equal(untouched, member));
            Assert.All(joins, word => Assert.Equal(ulong.MaxValue, word));
        }
    }

    [Fact]
    public void HiddenCrateUnderSixtyDegreeWedge()
    {
        using SupportFootprintScene scene = SupportFootprintScenes.HiddenCrate(FootprintPartition.Boxes);
        double installedCrateTop = scene.Faces.Find(f => f.Static == scene.Crate && f.Normal.Y > 0)!.Vertices[0].Y;
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        SupportSample sample = FootSupport.Find(null, null, scene.World, lease, query);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(scene.Crate, sample.Static);
        Assert.True(Math.Abs(sample.Height - installedCrateTop) <= sample.HeightError);
        Assert.True(sample.HeightError <= 0.0005f);
    }
}
