using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public sealed class WorldConfigurationRefusalTests
{
    [Theory]
    [InlineData("server")]
    [InlineData("sharded")]
    [InlineData("client")]
    public void Unreliable_opt_in_requires_a_replication_stream(string head)
    {
        using var hub = new InMemoryTransportHub();
        IDisposable Create(ReplicationStreamOptions? stream) => head switch
        {
            "server" => new WorldServer(hub.Server, new()
            { DeltaReplication = true, AllowUnreliableDeltaReplication = true, ReplicationStream = stream! },
                (_, _) => 0, MoveTuning.Default),
            "sharded" => new ShardedWorldServer(hub.Server, new()
            { DeltaReplication = true, AllowUnreliableDeltaReplication = true, ReplicationStream = stream! },
                (_, _) => 0, MoveTuning.Default),
            _ => new WorldClient(hub.CreateClient(), (_, _) => 0, MoveTuning.Default, new()
            { RequestDeltaReplication = true, RequestUnreliableDeltaReplication = true, ReplicationStream = stream! }),
        };

        WorldConfigurationException refusal = Assert.Throws<WorldConfigurationException>(() => Create(null));

        Assert.Equal(WorldConfigurationRefusal.ReplicationStreamMissing, refusal.Reason);
        Assert.Equal("config", refusal.ParamName);
        Assert.Equal("ReplicationStream", refusal.MemberName);
        using IDisposable control = Create(new ReplicationStreamOptions());
    }

    [Fact]
    public void Interest_must_fit_the_overlap_and_equality_is_admitted()
    {
        using var hub = new InMemoryTransportHub();
        WorldConfigurationException refusal = Assert.Throws<WorldConfigurationException>(() =>
            new ShardedWorldServer(hub.Server, new() { InterestRadius = 25f, OverlapMargin = 24f },
                (_, _) => 0, MoveTuning.Default));

        Assert.Equal(WorldConfigurationRefusal.InterestOutsideOverlap, refusal.Reason);
        Assert.Equal("config", refusal.ParamName);
        Assert.Equal(25f, refusal.InterestRadius);
        Assert.Equal(24f, refusal.OverlapMargin);
        using var control = new ShardedWorldServer(hub.Server, new() { InterestRadius = 24f, OverlapMargin = 24f },
            (_, _) => 0, MoveTuning.Default);
    }

    [Fact]
    public void Nonfinite_frame_planar_magnitude_has_its_own_reason()
    {
        using var hub = new InMemoryTransportHub();
        WorldConfigurationException refusal = Assert.Throws<WorldConfigurationException>(() =>
            new ShardedWorldServer(hub.Server, new() { CellSize = float.PositiveInfinity }, (_, _) => 0, MoveTuning.Default));

        Assert.Equal(WorldConfigurationRefusal.FramePlanarMagnitudeNonfinite, refusal.Reason);
        Assert.Equal("config", refusal.ParamName);
        Assert.Equal(float.PositiveInfinity, refusal.CellSize);
        Assert.Equal(24f, refusal.OverlapMargin);
        Assert.Equal(double.PositiveInfinity, refusal.ActualPlanarMagnitude);
        Assert.Null(refusal.MaximumPlanarMagnitude);
        using var control = new ShardedWorldServer(hub.Server, new() { CellSize = 60f }, (_, _) => 0, MoveTuning.Default);
    }

    [Fact]
    public void Incompatible_members_are_an_owned_read_only_snapshot()
    {
        string[] source = ["ReceiveServerTick", "RequestUnreliableDeltaReplication"];
        var refusal = new WorldConfigurationException(WorldConfigurationRefusal.ServerTickWithUnreliableReplication,
            "incompatible", "config") { IncompatibleMembers = source };
        source[0] = "changed";

        Assert.Equal(new[] { "ReceiveServerTick", "RequestUnreliableDeltaReplication" }, refusal.IncompatibleMembers);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)refusal.IncompatibleMembers!)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)refusal.IncompatibleMembers!).SyncRoot);
    }
}
