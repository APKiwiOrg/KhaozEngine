using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCombatContactContractTests
{
    [Fact]
    public void Defaults_match_the_approved_contract()
    {
        var settings = new TileCombatContactPresentationSettings();
        Assert.Equal(.02f, settings.ResponseSeconds);
        Assert.Equal(.15f, settings.ReleaseSeconds);
        Assert.Equal(4f, settings.MaxAdjustmentSpeedTilesPerSecond);
        Assert.Equal(3f, settings.MaxGoalOffsetTiles);
        Assert.Equal(.04f, settings.ContactToleranceTiles);
        Assert.Equal((byte)2, settings.TerminalHoldTicks);
        Assert.Equal((ushort)256, settings.MaxParticipants);
        Assert.Null(ContactContractScenario.Config().CombatContactPresentation);
        Assert.Equal(new[] { 0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 },
            new[]
            {
                (int)TileCombatContactLimits.None, (int)TileCombatContactLimits.MissingGeometry,
                (int)TileCombatContactLimits.LatePreparation, (int)TileCombatContactLimits.LateOutcome,
                (int)TileCombatContactLimits.ChangedGeometry, (int)TileCombatContactLimits.ConflictingLayout,
                (int)TileCombatContactLimits.GoalDistance, (int)TileCombatContactLimits.ParticipantCapacity,
                (int)TileCombatContactLimits.Collision, (int)TileCombatContactLimits.ReleasePath,
                (int)TileCombatContactLimits.TargetUnavailable, (int)TileCombatContactLimits.PresentationCut
            });
    }

    public static IEnumerable<object[]> InvalidSettings()
    {
        float[] invalid = [0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        foreach (float value in invalid)
        {
            yield return [new TileCombatContactPresentationSettings { ResponseSeconds = value }];
            yield return [new TileCombatContactPresentationSettings { ReleaseSeconds = value }];
            yield return [new TileCombatContactPresentationSettings { MaxAdjustmentSpeedTilesPerSecond = value }];
            yield return [new TileCombatContactPresentationSettings { MaxGoalOffsetTiles = value }];
            yield return [new TileCombatContactPresentationSettings { ContactToleranceTiles = value }];
        }
        yield return [new TileCombatContactPresentationSettings { ContactToleranceTiles = 3f }];
        yield return [new TileCombatContactPresentationSettings { ContactToleranceTiles = 4f }];
        yield return [new TileCombatContactPresentationSettings { TerminalHoldTicks = 0 }];
        yield return [new TileCombatContactPresentationSettings { MaxParticipants = 0 }];
        yield return [new TileCombatContactPresentationSettings { MaxParticipants = 1 }];
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Invalid_settings_fail_construction(TileCombatContactPresentationSettings settings)
    {
        using var hub = new InMemoryTransportHub();
        using INetTransport transport = hub.CreateClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileWorldClient(transport,
            ContactContractScenario.Config(settings, preparationEnabled: true), new TileCollisionMap(1)));
    }

    [Fact]
    public void Minimum_valid_bounds_allow_construction()
    {
        using var hub = new InMemoryTransportHub();
        using INetTransport transport = hub.CreateClient();
        using var client = new TileWorldClient(transport, ContactContractScenario.Config(
            new TileCombatContactPresentationSettings { TerminalHoldTicks = 1, MaxParticipants = 2 },
            preparationEnabled: true), new TileCollisionMap(1));
    }

    [Fact]
    public void Contact_requires_preparation()
    {
        using var hub = new InMemoryTransportHub();
        using INetTransport transport = hub.CreateClient();
        Assert.Throws<ArgumentException>(() => new TileWorldClient(transport,
            ContactContractScenario.Config(new TileCombatContactPresentationSettings()), new TileCollisionMap(1)));
    }

    [Fact]
    public void Disabled_body_reads_match_raw_exactly()
    {
        using var scenario = new ContactContractScenario();
        scenario.Client.Presenter = new TilePresenter(2f, 4f);
        TileMoveState remote = TileMoveState.At(new TileCoord(22, 21, 0), TileDirection.E);
        remote.StepFrom = new TileCoord(21, 21, 0);
        remote.StepTotal = 4;
        remote.StepTicks = 2;
        scenario.Snapshot(10, (ContactContractScenario.RemoteId, remote));
        scenario.Client.AdvancePresentation(.125f);

        AssertRaw(scenario.Client, scenario.Client.LocalNetId, scenario.Client.LocalPose, 10);
        Assert.True(scenario.Client.TryGetRemotePose(ContactContractScenario.RemoteId, out TilePose raw));
        AssertRaw(scenario.Client, ContactContractScenario.RemoteId, raw, 10);
        Assert.Empty(scenario.Client.CombatContactImpacts);
        Assert.Equal(0L, scenario.Client.CombatContactMissCount);
        Assert.False(scenario.Client.TryTransferCombatBodyPresentation(scenario.Client.LocalNetId,
            ContactContractScenario.RemoteId));
    }

    [Fact]
    public void Enabled_contract_initially_returns_raw_samples()
    {
        using var scenario = new ContactContractScenario(new TileCombatContactPresentationSettings());
        scenario.Snapshot(10, (ContactContractScenario.RemoteId,
            TileMoveState.At(new TileCoord(22, 20, 0), TileDirection.S)));
        scenario.Client.AdvancePresentation(.1f);
        AssertRaw(scenario.Client, scenario.Client.LocalNetId, scenario.Client.LocalPose, 10);
        Assert.True(scenario.Client.TryGetRemotePose(ContactContractScenario.RemoteId, out TilePose raw));
        AssertRaw(scenario.Client, ContactContractScenario.RemoteId, raw, 10);
        Assert.Empty(scenario.Client.CombatContactImpacts);
        Assert.Equal(0L, scenario.Client.CombatContactMissCount);
    }

    [Fact]
    public void Rejected_snapshot_headers_do_not_replace_the_body_source_tick()
    {
        using var scenario = new ContactContractScenario();
        scenario.Snapshot(10, (ContactContractScenario.RemoteId,
            TileMoveState.At(new TileCoord(22, 20, 0), TileDirection.S)));
        scenario.Client.AdvancePresentation(.1f);
        scenario.ApplySnapshot(500, Array.Empty<byte>());
        Assert.Equal(500L, scenario.Client.ServerTick);
        Assert.Equal(1, scenario.Client.DroppedSnapshotCount);
        AssertRaw(scenario.Client, scenario.Client.LocalNetId, scenario.Client.LocalPose, 10);
        Assert.True(scenario.Client.TryGetRemotePose(ContactContractScenario.RemoteId, out TilePose raw));
        AssertRaw(scenario.Client, ContactContractScenario.RemoteId, raw, 10);
        scenario.Snapshot(11);
        AssertRaw(scenario.Client, scenario.Client.LocalNetId, scenario.Client.LocalPose, 11);
    }

    [Fact]
    public void Joined_client_has_no_contact_body_before_a_successful_movement_snapshot()
    {
        using var scenario = new ContactContractScenario(seed: false);
        Assert.True(scenario.Client.IsJoined);
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(scenario.Client.LocalNetId, out _));
        scenario.ApplySnapshot(500, Array.Empty<byte>());
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(scenario.Client.LocalNetId, out _));
        Assert.Empty(scenario.Client.CombatContactImpacts);
        Assert.Equal(0L, scenario.Client.CombatContactMissCount);
    }

    [Fact]
    public void Disposal_clears_contact_body_reads()
    {
        using var scenario = new ContactContractScenario(new TileCombatContactPresentationSettings());
        Assert.True(scenario.Client.TryGetCombatBodyPresentation(scenario.Client.LocalNetId, out _));
        scenario.Client.Dispose();
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(scenario.Client.LocalNetId, out _));
        Assert.Empty(scenario.Client.CombatContactImpacts);
        Assert.Equal(0L, scenario.Client.CombatContactMissCount);
        Assert.False(scenario.Client.TryTransferCombatBodyPresentation(scenario.Client.LocalNetId, 9999));
    }

    [Fact]
    public void Unjoined_unknown_and_removed_bodies_have_no_contact_pose()
    {
        using var hub = new InMemoryTransportHub();
        using INetTransport transport = hub.CreateClient();
        using var unjoined = new TileWorldClient(transport, ContactContractScenario.Config(), new TileCollisionMap(1));
        Assert.False(unjoined.TryGetCombatBodyPresentation(unjoined.LocalNetId, out _));
        Assert.False(unjoined.TryGetCombatBodyPresentation(ContactContractScenario.RemoteId, out _));

        using var scenario = new ContactContractScenario();
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(9999, out _));
        scenario.Snapshot(10, (ContactContractScenario.RemoteId,
            TileMoveState.At(new TileCoord(22, 20, 0), TileDirection.S)));
        scenario.Client.AdvancePresentation(.1f);
        Assert.True(scenario.Client.TryGetCombatBodyPresentation(ContactContractScenario.RemoteId, out _));
        scenario.Snapshot(11);
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(ContactContractScenario.RemoteId, out _));
        scenario.Client.AdvancePresentation(.1f);
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(ContactContractScenario.RemoteId, out _));
        scenario.ApplySnapshot(12, SnapshotWriter.Write(new World(), TileProtocol.CreateRegistry()));
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(scenario.Client.LocalNetId, out _));
        scenario.Snapshot(13);
        scenario.Hub.Server.Disconnect(new NetConnectionId(1));
        scenario.Client.Poll();
        Assert.False(scenario.Client.TryGetCombatBodyPresentation(scenario.Client.LocalNetId, out _));
    }

    static void AssertRaw(TileWorldClient client, long netId, TilePose raw, long tick)
    {
        Assert.True(client.TryGetCombatBodyPresentation(netId, out TileCombatBodyPresentation shown));
        Assert.Equal(raw, shown.Pose);
        Assert.Equal(Vector3.Zero, shown.Correction);
        Assert.Equal(Vector3.Zero, shown.Velocity);
        Assert.Equal(Vector3.Zero, shown.BaseVelocity);
        Assert.Equal(tick, shown.SourceServerTick);
        Assert.False(shown.Discontinuity);
        Assert.Equal(TileCombatContactLimits.None, shown.Limits);
    }
}

internal sealed class ContactContractScenario : IDisposable
{
    internal const long RemoteId = 100;
    internal readonly InMemoryTransportHub Hub = new();
    internal readonly TileWorldClient Client;
    readonly TileWorldServer server;
    readonly INetTransport transport;

    internal ContactContractScenario(TileCombatContactPresentationSettings? settings = null, bool seed = true)
    {
        TileWorldDocument document = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(document);
        server = new TileWorldServer(Hub.Server, TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
        {
            CombatPreparationRules = settings is null ? null : new PreparationScenario.Profiles()
        }, map, new TileDocumentTargets(document, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        transport = Hub.CreateClient();
        Client = new TileWorldClient(transport, Config(settings, settings is not null), map);
        Client.Tick(.06f);
        Client.Poll();
        server.Poll();
        if (seed) server.Tick(.25f);
        Client.Poll();
        Assert.True(Client.IsJoined);
    }

    internal static TileWorldClientConfig Config(TileCombatContactPresentationSettings? settings = null,
        bool preparationEnabled = false) => new()
    {
        TickSeconds = .25f,
        StepTicks = new TileStepTicks(4, 2),
        CombatPreparationEnabled = preparationEnabled,
        CombatContactPresentation = settings
    };

    internal byte[] Payload(params (long Id, TileMoveState State)[] remotes)
    {
        var world = new World();
        Add(Client.LocalNetId, TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N));
        foreach (var remote in remotes) Add(remote.Id, remote.State);
        return SnapshotWriter.Write(world, TileProtocol.CreateRegistry(), ownerNetId: Client.LocalNetId);

        void Add(long id, TileMoveState state)
        {
            Entity entity = world.Spawn();
            world.Set(entity, new NetId(id));
            world.Set(entity, state);
        }
    }

    internal void Snapshot(long tick, params (long Id, TileMoveState State)[] remotes) =>
        ApplySnapshot(tick, Payload(remotes));

    internal void ApplySnapshot(long tick, byte[] payload)
    {
        byte[] frame = TileProtocol.EncodeSnapshotFrame(Client.LocalNetId, 0, tick, payload);
        Hub.Server.Send(new NetConnectionId(1), SessionFrame.Write(SessionOpcode.Data, frame),
            NetChannelReliability.ReliableOrdered);
        Client.Poll();
    }

    public void Dispose()
    {
        Client.Dispose();
        server.Dispose();
        transport.Dispose();
        Hub.Dispose();
    }
}
