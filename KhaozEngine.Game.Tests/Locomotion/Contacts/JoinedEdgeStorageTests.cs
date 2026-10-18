using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>SG-22 at the arena's row, key and dry nesting limits, through real support reads.</summary>
[Collection("AllocSensitive")]
public sealed class JoinedEdgeStorageTests
{
    static readonly FootSupportQuery Query = new(Vector2.Zero, 0, 0.2f, 0.5f, 0.5f, 0.7f);

    [Fact]
    public void JoinedEdgeRowOverflowKeepsGeometryWithoutAllocating()
    {
        using var scene = new FootSupportScene(SceneVariant.Box).Flat("floor", -2, 2, -2, 2, 0);
        using var interval = new JoinedEdgeContinuityTests.OpenInterval(scene);
        var samples = new SupportSample[65];
        // Prime Find once, then reset retention. No sample in the measured interval is a memo repeat.
        SupportSample warm = Read(scene, interval.Memo, Query);
        Assert.Equal(0, warm.Joined.Count);
        interval.Memo.Close();
        interval.Memo.Open(scene.Lease);
        long bytes;
        using (SupportAllocationRegion.Start())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < samples.Length; i++)
                samples[i] = Read(scene, interval.Memo, Query with { FeetY = i * 0.00001f });
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
        Assert.Equal(64, interval.Memo.Arena.Count);
        for (int i = 0; i < 64; i++)
        {
            Assert.False(samples[i].Continuity.IsEmpty);
            Assert.Equal(0, samples[i].Joined.Count);
        }
        AssertOverflow(scene, Query with { FeetY = 64 * 0.00001f }, samples[64]);
    }

    [Fact]
    public void JoinedEdgeKeyOverflowKeepsWholeEarlierRowsWithoutAllocating()
    {
        using FootSupportScene scene = JoinedEdgeContinuityTests.Fan(256);
        using var interval = new JoinedEdgeContinuityTests.OpenInterval(scene);
        var samples = new SupportSample[6];
        SupportSample warm = Read(scene, interval.Memo, Query);
        Assert.Equal(255, warm.Joined.Count);
        interval.Memo.Close();
        interval.Memo.Open(scene.Lease);
        long bytes;
        using (SupportAllocationRegion.Start())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < samples.Length; i++)
                samples[i] = Read(scene, interval.Memo, Query with { FeetY = i * 0.00001f });
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
        Assert.Equal(4, interval.Memo.Arena.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.False(samples[i].Continuity.IsEmpty);
            Assert.Equal(255, samples[i].Joined.Count);
            for (int member = 0; member < 256; member++)
                Assert.Equal(member != samples[i].FeatureId,
                    samples[i].Joined.Contains(new(scene["fan"], member)));
        }
        // Four complete rows consume 1,020 keys. Neither overflow may retain a four-key prefix or consume a row.
        AssertOverflow(scene, Query with { FeetY = 4 * 0.00001f }, samples[4]);
        AssertOverflow(scene, Query with { FeetY = 5 * 0.00001f }, samples[5]);
    }

    [Fact]
    public void JoinedEdgeDryNestingIsPreprovisionedAndBounded()
    {
        using FootSupportScene scene = JoinedEdgeContinuityTests.Partition("split-statics");
        var query = Query with { Axis = new(0.1f, -0.125f) };
        var memos = new SupportRequestMemo?[4];
        var samples = new SupportSample[4];
        SupportRequestMemo? warm = SupportRequestMemo.OpenDry(scene.Lease);
        Assert.NotNull(warm);
        Read(scene, warm, query);
        warm.Close();
        long bytes;
        try
        {
            using (SupportAllocationRegion.Start())
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < memos.Length; i++)
                {
                    memos[i] = SupportRequestMemo.OpenDry(scene.Lease);
                    samples[i] = Read(scene, memos[i], query);
                }
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.Equal(0, bytes);
            for (int i = 0; i < memos.Length; i++)
            {
                Assert.NotNull(memos[i]);
                Assert.False(samples[i].Continuity.IsEmpty);
                Assert.Same(memos[i]!.Arena, samples[i].Continuity.Arena);
                for (int j = 0; j < i; j++) Assert.NotSame(memos[j], memos[i]);
            }
            Assert.Throws<InvalidOperationException>(() => SupportRequestMemo.OpenDry(scene.Lease));
            // Closing and reusing the deepest interval invalidates only its own old handles.
            memos[3]!.Close();
            memos[3] = SupportRequestMemo.OpenDry(scene.Lease);
            SupportSample fresh = Read(scene, memos[3], query);
            Assert.Throws<InvalidOperationException>(() => samples[3].Source);
            Assert.Equal(query, fresh.Source.Query);
            Assert.Equal(query, samples[0].Source.Query);
        }
        finally
        {
            for (int i = memos.Length - 1; i >= 0; i--) memos[i]?.Close();
        }
    }

    static SupportSample Read(FootSupportScene scene, SupportRequestMemo? memo, in FootSupportQuery query) =>
        FootSupport.Find(null, scene.World, scene.Lease, query, null, memo);

    static void AssertOverflow(FootSupportScene scene, in FootSupportQuery query, in SupportSample sample)
    {
        Assert.True(sample.Continuity.IsEmpty);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(FootSupport.Find(null, scene.World, scene.Lease, query), sample);
        Assert.Equal(0, sample.Joined.Count);
    }
}
