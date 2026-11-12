using System;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public sealed class RebuildStagingWorldTickTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format2_staging_discards_tracking_after_accepted_or_failed_decoding(bool malformed)
    {
        // #1771: stable interest still creates a fresh private staging generation for every packet.
        var registry = new ReplicationRegistry();
        registry.Register<RebuildValue>(RebuildLink.ValueId, (v, writer) => writer.Write(v.Number),
            reader => new RebuildValue { Number = reader.ReadInt32() });
        registry.Register<RebuildTag>(RebuildLink.TagId, (_, _) => { }, _ =>
            malformed ? throw new InvalidOperationException("Refused test tag.") : default);
        var link = new RebuildLink(clientRegistry: registry);
        var entity = link.Spawn(41, 7);
        link.ServerWorld.Set(entity, new RebuildTag());

        for (int packet = 0; packet < 64; packet++)
        {
            link.Live.AdvanceTick(); // The direct live-world owner supplies its receive boundary.
            ulong before = link.Client.StagingTrackingForTests.Tick;
            ReplicationDeltaPacket sent = link.Send();
            Assert.Equal(malformed ? DeltaRebuildResult.Invalid : DeltaRebuildResult.Accepted, link.Deliver(sent));
            if (malformed)
            {
                // Value decoded before the tag reader failed. Neither partial state nor tracking may survive.
                Assert.Equal(DeltaRebuildFailure.MalformedPacket, link.Client.LastFailure);
                Assert.Empty(link.View.Entities);
            }
            else
            {
                Assert.Equal(7, link.Live.Get<RebuildValue>(link.LiveEntity(41)).Number);
                link.Ack(sent);
            }
            Assert.Equal((0, 0, 0, 0, 0), link.Client.StagingTrackingForTests.Counts);
            Assert.True(link.Client.StagingTrackingForTests.Tick > before);
        }
    }
}
