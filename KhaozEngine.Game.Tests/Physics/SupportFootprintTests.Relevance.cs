using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(300, false)]
    [InlineData(300, true)]
    public void BelowBandFanJoinedToBandCapableSlopeRemainsRelevant(int count, bool reverse)
    {
        using var scene = new SupportFootprintScene();
        PhysicsShape fan = RelevanceFan(count, -0.40005f);
        PhysicsShape slope = RelevanceSlope(-0.39998f, 0.00001f);
        StaticHandle fanOwner, slopeOwner;
        if (reverse)
        {
            slopeOwner = scene.Install(slope, Pose.Identity);
            fanOwner = scene.Install(fan, Pose.Identity);
        }
        else
        {
            fanOwner = scene.Install(fan, Pose.Identity);
            slopeOwner = scene.Install(slope, Pose.Identity);
        }
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256);
        var joins = Enumerable.Repeat(99UL, 1024).ToArray();
        SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease,
            Request(lower: -(double)0.4f), members, joins);
        if (count > 256)
        {
            Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, result.Status);
            Assert.Equal(257, result.RequiredElements);
            Assert.Equal(MemberSentinels(256), members);
            Assert.All(joins, word => Assert.Equal(99UL, word));
            return;
        }
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(count + 1, result.Members);
        int slopeIndex = Array.FindIndex(members, 0, result.Members, m => m.Static == slopeOwner);
        foreach (int index in Enumerable.Range(0, result.Members).Where(i => members[i].Static == fanOwner))
        {
            Assert.True(members[index].DiscHeightUpper < -(double)0.4f);
            Assert.True((joins[slopeIndex * result.JoinWordsPerRow + index / 64] & (1UL << (index % 64))) != 0);
        }
        Assert.True(members[slopeIndex].HasDiscMaximum);
        // Every fan plane caps the slope below the inclusive band bottom.
        Assert.True(members[slopeIndex].DiscHeightLower > -(double)0.4f);
    }

    [Fact]
    public void BelowBandMemberInAnInBandProposalContextIsRetainedWithoutAJoin()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle floor = scene.Install(RelevanceFan(16, -0.40005f), Pose.Identity);
        StaticHandle slope = scene.Install(RelevanceSlope(-0.39998f, 0.01f), Pose.Identity);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease,
            Request(lower: -(double)0.4f), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.True(result.AxisVerdict.LowestLower >= -(double)0.4f);
        int slopeIndex = Array.FindIndex(members, 0, result.Members, m => m.Static == slope);
        Assert.Contains(members.Take(result.Members), m => m.Static == floor && (m.ProposalMask & 1) != 0);
        foreach (int index in Enumerable.Range(0, result.Members).Where(i => members[i].Static == floor))
            Assert.Equal(0UL, joins[slopeIndex * result.JoinWordsPerRow + index / 64] & (1UL << (index % 64)));
    }

    [Fact]
    public void WarmBelowBandTraversalAllocatesNothing()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(RelevanceFan(300, -0.4005f), Pose.Identity);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var request = Request(lower: -(double)0.4f);
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        IPhysicsSupportFootprint capability = Capability(scene.World);
        using var region = SupportAllocationRegion.Start();
        Assert.Equal(CapsuleFeatureStatus.Complete, capability.QuerySupportFootprint(lease, request, members, joins).Status);
        long before = GC.GetAllocatedBytesForCurrentThread();
        SupportFootprintResult warm = capability.QuerySupportFootprint(lease, request, members, joins);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, warm.Members);
        Assert.Equal(0, bytes);
    }

    static PhysicsShape RelevanceSlope(float height, float gradient) => new TriangleMeshShape(
        [new(-0.5f, height - 0.5f * gradient, -0.5f), new(-0.5f, height - 0.5f * gradient, 0.5f),
            new(0.5f, height + 0.5f * gradient, 0)], [0, 2, 1]);

    static PhysicsShape RelevanceFan(int count, float height)
    {
        var vertices = new Vector3[count * 3];
        var indices = new int[vertices.Length];
        for (int i = 0; i < count; i++)
        {
            float a = 2 * MathF.PI * i / count, b = 2 * MathF.PI * (i + 1) / count;
            vertices[3 * i] = new(0, height, 0);
            vertices[3 * i + 1] = new(2 * MathF.Cos(a), height, 2 * MathF.Sin(a));
            vertices[3 * i + 2] = new(2 * MathF.Cos(b), height, 2 * MathF.Sin(b));
            for (int j = 0; j < 3; j++) indices[3 * i + j] = 3 * i + j;
        }
        return new TriangleMeshShape(vertices, indices);
    }
}
