using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// A built-in id (below <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) may carry
/// <see cref="ReplicationChannels.OwnerOnly"/> on top of <see cref="ReplicationChannels.Default"/> and nothing else
/// (<see cref="ReplicationRegistry.BuiltinChannelsAllowed"/>). Built-in frames are unframed, so the property that
/// makes this safe is that OwnerOnly drops the WHOLE frame for a non-owner: the frames around it stay aligned and
/// decode, on the snapshot path and the delta path, while persistence and handoff keep writing it.
/// </summary>
public class BuiltinOwnerOnlyChannelTests
{
    private struct Before : IComponent { public int V; }   // id 1, built-in, Default
    private struct Private : IComponent { public long V; } // id 2, built-in, Default | OwnerOnly
    private struct After : IComponent { public short V; }  // id 3, built-in, Default

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Before>(1, (c, bw) => bw.Write(c.V), br => new Before { V = br.ReadInt32() });
        r.Register<Private>(2, (c, bw) => bw.Write(c.V), br => new Private { V = br.ReadInt64() },
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        r.Register<After>(3, (c, bw) => bw.Write(c.V), br => new After { V = br.ReadInt16() });
        return r;
    }

    private static World TwoPlayers()
    {
        var w = new World();
        for (int id = 1; id <= 2; id++)
        {
            Entity e = w.Spawn();
            w.Set(e, new NetId(id));
            w.Set(e, new Before { V = 10 * id });
            w.Set(e, new Private { V = 1000L * id });
            w.Set(e, new After { V = (short)(100 * id) });
        }
        return w;
    }

    [Fact]
    public void A_builtin_accepts_Default_with_OwnerOnly()
    {
        Assert.True(ReplicationRegistry.BuiltinChannelsAllowed(ReplicationChannels.Default));
        Assert.True(ReplicationRegistry.BuiltinChannelsAllowed(ReplicationChannels.Default | ReplicationChannels.OwnerOnly));
        Registry();   // registers id 2 as Default | OwnerOnly without throwing
    }

    [Theory]
    [InlineData(ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly)]
    [InlineData(ReplicationChannels.Replicate | ReplicationChannels.Persist | ReplicationChannels.OwnerOnly)]
    [InlineData(ReplicationChannels.Persist | ReplicationChannels.Migrate)]
    [InlineData(ReplicationChannels.Replicate)]
    public void A_builtin_refuses_any_set_that_drops_a_channel(ReplicationChannels channels)
    {
        Assert.False(ReplicationRegistry.BuiltinChannelsAllowed(channels));
        var r = new ReplicationRegistry();
        Assert.Throws<ArgumentException>(() =>
            r.Register<Private>(2, (c, bw) => bw.Write(c.V), br => new Private { V = br.ReadInt64() }, channels: channels));
    }

    [Fact]
    public void The_snapshot_path_drops_the_whole_frame_for_a_non_owner_and_stays_aligned()
    {
        ReplicationRegistry r = Registry();
        World w = TwoPlayers();
        byte[] snapshot = SnapshotWriter.WriteFiltered(w, r, new HashSet<long> { 1, 2 }, ReplicationChannels.Replicate, 1);

        var client = new World();
        var view = new ClientReplicationView(r);
        view.Apply(client, snapshot);

        Assert.True(view.TryGetEntity(1, out Entity own));
        Assert.Equal(1000L, client.Get<Private>(own).V);
        Assert.Equal((short)100, client.Get<After>(own).V);

        Assert.True(view.TryGetEntity(2, out Entity other));
        Assert.False(client.Has<Private>(other));
        Assert.Equal(20, client.Get<Before>(other).V);
        Assert.Equal((short)200, client.Get<After>(other).V);   // read from the right bytes: nothing was skipped
    }

    [Fact]
    public void The_delta_path_drops_the_whole_frame_for_a_non_owner_and_stays_aligned()
    {
        ReplicationRegistry r = Registry();
        World w = TwoPlayers();
        var ids = new HashSet<long> { 1, 2 };
        var repl = new AoiDeltaReplicator(r);
        repl.BeginTick();
        byte[] delta = repl.WriteFor(0, w, ids, ownerNetId: 2);

        var client = new World();
        var view = new ClientReplicationView(r);
        view.ApplyDelta(client, delta);

        Assert.True(view.TryGetEntity(1, out Entity other));
        Assert.False(client.Has<Private>(other));
        Assert.Equal((short)100, client.Get<After>(other).V);
        Assert.True(view.TryGetEntity(2, out Entity own));
        Assert.Equal(2000L, client.Get<Private>(own).V);
        Assert.Equal((short)200, client.Get<After>(own).V);
    }

    [Theory]
    [InlineData(ReplicationChannels.Persist)]
    [InlineData(ReplicationChannels.Migrate)]
    public void Persistence_and_handoff_write_an_owner_only_builtin_for_everyone(ReplicationChannels channel)
    {
        ReplicationRegistry r = Registry();
        World w = TwoPlayers();
        byte[] snapshot = SnapshotWriter.WriteFiltered(w, r, new HashSet<long> { 1, 2 }, channel, ownerNetId: null);

        var restored = new World();
        var view = new ClientReplicationView(r);
        view.Apply(restored, snapshot);
        Assert.True(view.TryGetEntity(1, out Entity one));
        Assert.True(view.TryGetEntity(2, out Entity two));
        Assert.Equal(1000L, restored.Get<Private>(one).V);
        Assert.Equal(2000L, restored.Get<Private>(two).V);
    }

    /// <summary>
    /// Owner scoping is no longer a copy per entity per client. The owner is handed the captured set itself, and
    /// every other viewer the one public view, built once per capture. That is what keeps every movement server,
    /// whose registry now always carries an owner-only built-in, off a per-client allocation per observed player.
    /// </summary>
    [Fact]
    public void Owner_scoping_shares_one_public_view_across_every_non_owner()
    {
        ReplicationRegistry r = Registry();
        World w = TwoPlayers();
        Dictionary<long, CapturedComponents> capture = new CaptureScratch().CaptureReplicate(w, r);
        CapturedComponents two = capture[2];

        Assert.Same(two, CaptureProjection.OwnerScope(two, r, netId: 2, ownerNetId: 2));
        CapturedComponents seenByOne = CaptureProjection.OwnerScope(two, r, netId: 2, ownerNetId: 1);
        Assert.Same(seenByOne, CaptureProjection.OwnerScope(two, r, netId: 2, ownerNetId: 7));
        Assert.Same(seenByOne, CaptureProjection.OwnerScope(two, r, netId: 2, ownerNetId: null));
        Assert.False(seenByOne.Contains(2));
        Assert.True(seenByOne.Contains(1));
        Assert.True(seenByOne.Contains(3));
        Assert.Equal(two.Order, seenByOne.Order);
    }

    [Fact]
    public void An_entity_with_nothing_owner_only_is_its_own_public_view()
    {
        ReplicationRegistry r = Registry();
        var w = new World();
        Entity e = w.Spawn();
        w.Set(e, new NetId(5));
        w.Set(e, new Before { V = 1 });
        CapturedComponents five = new CaptureScratch().CaptureReplicate(w, r)[5];

        Assert.Same(five, CaptureProjection.OwnerScope(five, r, netId: 5, ownerNetId: 1));
    }
}
