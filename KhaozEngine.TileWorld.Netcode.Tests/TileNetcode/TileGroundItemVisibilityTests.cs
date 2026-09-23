using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>Per-viewer ground visibility through two real client snapshot mirrors.</summary>
public sealed class TileGroundItemVisibilityTests
{
    [Fact]
    public void PrivateGroundEntityAndInstanceReachOnlyTheOwnerWhilePublicGroundReachesBoth()
    {
        using var pair = new Pair();
        pair.Frames(12);
        Assert.Equal(2, pair.Server.PlayerCount);

        long privateId = pair.Server.SpawnGroundItem(new TileCoord(12, 10, 0), 7, 1, 1000,
            instanceId: 71, payload: [1, 2]);
        pair.PrivateId = privateId;
        long publicId = pair.Server.SpawnGroundItem(new TileCoord(12, 11, 0), 8, 1, 1000);
        pair.Frames(12);

        Assert.Equal([privateId, publicId], pair.GroundIds(pair.Owner));
        Assert.Equal([publicId], pair.GroundIds(pair.Other));
        Assert.True(pair.Owner.View.Entities.ContainsKey(privateId));
        Assert.False(pair.Other.View.Entities.ContainsKey(privateId));
        Assert.Contains(privateId, pair.Server.ServeInterest(0));
        Assert.DoesNotContain(privateId, pair.Server.ServeInterest(1));
        Assert.Contains(pair.Other.LocalNetId, pair.Owner.RemoteNetIds);
        Assert.Contains(pair.Owner.LocalNetId, pair.Other.RemoteNetIds);
    }

    [Fact]
    public void PolicyChangesRemoveAndRestoreARealClientGroundEntity()
    {
        using var pair = new Pair();
        pair.Frames(12);
        long privateId = pair.Server.SpawnGroundItem(new TileCoord(12, 10, 0), 7, 1, 1000);
        pair.PrivateId = privateId;
        pair.AllowOther = true;
        pair.Frames(12);
        Assert.Contains(privateId, pair.GroundIds(pair.Other));

        pair.AllowOther = false;
        pair.Frames(12);
        Assert.DoesNotContain(privateId, pair.GroundIds(pair.Other));

        pair.AllowOther = true;
        pair.Frames(12);
        Assert.Contains(privateId, pair.GroundIds(pair.Other));
    }

    [Fact]
    public void PlaneFilterStillWinsWhenTheGroundPredicateAllowsEveryone()
    {
        using var pair = new Pair();
        pair.Frames(12);
        pair.AllowOther = true;
        long upstairs = pair.Server.SpawnGroundItem(new TileCoord(12, 10, 1), 9, 1, 1000);
        pair.PrivateId = upstairs;
        pair.Frames(12);

        Assert.DoesNotContain(upstairs, pair.GroundIds(pair.Owner));
        Assert.DoesNotContain(upstairs, pair.GroundIds(pair.Other));
    }

    [Fact]
    public void AViewerEnteringRangeLaterStillNeverReceivesThePrivateGroundEntity()
    {
        using var pair = new Pair();
        pair.Frames(12);
        pair.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(40, 40, 0), TileDirection.S));
        pair.Frames(8);
        long privateId = pair.Server.SpawnGroundItem(new TileCoord(12, 10, 0), 7, 1, 1000);
        pair.PrivateId = privateId;
        long publicId = pair.Server.SpawnGroundItem(new TileCoord(12, 11, 0), 8, 1, 1000);
        pair.Frames(8);
        Assert.Empty(pair.GroundIds(pair.Other));

        pair.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(10, 11, 0), TileDirection.S));
        pair.Frames(12);
        Assert.Equal([publicId], pair.GroundIds(pair.Other));
        Assert.Equal([privateId, publicId], pair.GroundIds(pair.Owner));
    }

    [Fact]
    public void AReconnectingViewerStillNeverReceivesThePrivateGroundEntity()
    {
        using var pair = new Pair();
        pair.Frames(12);
        long privateId = pair.Server.SpawnGroundItem(new TileCoord(12, 10, 0), 7, 1, 1000);
        pair.PrivateId = privateId;
        long publicId = pair.Server.SpawnGroundItem(new TileCoord(12, 11, 0), 8, 1, 1000);
        pair.Frames(8);
        Assert.Equal([publicId], pair.GroundIds(pair.Other));

        pair.ReconnectOther();
        pair.Frames(12);
        Assert.Equal(2, pair.Server.PlayerCount);
        Assert.Equal([publicId], pair.GroundIds(pair.Other));
        Assert.Equal([privateId, publicId], pair.GroundIds(pair.Owner));
    }

    [Fact]
    public void NullPredicateKeepsLegacyPublicGroundVisibility()
    {
        using var pair = new Pair(filter: false);
        pair.Frames(12);
        long item = pair.Server.SpawnGroundItem(new TileCoord(12, 10, 0), 7, 1, 1000);
        pair.PrivateId = item;
        pair.Frames(12);

        Assert.Equal([item], pair.GroundIds(pair.Owner));
        Assert.Equal([item], pair.GroundIds(pair.Other));
    }

    [Fact]
    public void GroundPredicateCannotFilterPlayersOrObjectStates()
    {
        using var pair = new Pair();
        pair.Frames(12);
        pair.PrivateId = pair.Owner.LocalNetId;
        pair.Frames(8);
        Assert.Contains(pair.Owner.LocalNetId, pair.Other.RemoteNetIds);

        long objectNetId = pair.Server.SetObjectState(412, 1, new TileCoord(12, 10, 0));
        pair.PrivateId = objectNetId;
        pair.Frames(8);
        Assert.True(pair.Other.TryGetObjectState(412, out int state));
        Assert.Equal(1, state);
    }

    sealed class Pair : IDisposable
    {
        const float Tick = 0.25f;
        const float Frame = 0.05f;
        readonly InMemoryTransportHub _hub = new();
        INetTransport _otherTransport;
        float _accumulator;

        public TileWorldServer Server { get; }
        public TileWorldClient Owner { get; }
        public TileWorldClient Other { get; private set; }
        public long PrivateId { get; set; }
        public bool AllowOther { get; set; }

        public Pair(bool filter = true)
        {
            TileWorldDocument document = TileMoveSimulatorTests.FlatWorld(4);
            Server = new TileWorldServer(_hub.Server,
                TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)) with
                {
                    GroundItemVisibleToSlot = filter
                        ? (slot, id) => id != PrivateId || slot == 0 || AllowOther
                        : null,
                },
                TileMoveSimulatorTests.Bake(document),
                new TileDocumentTargets(document, TileMoveSimulatorTests.Catalogs),
                new AllowAllAuthenticator());
            Owner = Client(_hub.CreateClient());
            _otherTransport = _hub.CreateClient();
            Other = Client(_otherTransport);
            Owner.Tick(0.13f);
            Other.Tick(0.07f);
        }

        TileWorldClient Client(INetTransport transport) => new(transport, new TileWorldClientConfig
        {
            TickSeconds = Tick,
            StepTicks = new TileStepTicks(walk: 4, run: 2),
        }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld(4)));

        public long[] GroundIds(TileWorldClient client)
        {
            var drops = new List<(long NetId, TileGroundItem Item)>();
            client.CollectGroundItems(drops);
            return drops.Select(drop => drop.NetId).Order().ToArray();
        }

        public void ReconnectOther()
        {
            _hub.DisconnectClient(_otherTransport);
            Other.Dispose();
            Server.Poll();
            _otherTransport = _hub.CreateClient();
            Other = Client(_otherTransport);
            Other.Tick(0.07f);
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Owner.Tick(Frame);
                Other.Tick(Frame);
                Server.Poll();
                _accumulator += Frame;
                while (_accumulator >= Tick)
                {
                    _accumulator -= Tick;
                    Server.Tick(Tick);
                }
                Owner.Poll();
                Other.Poll();
                Owner.AdvancePresentation(Frame);
                Other.AdvancePresentation(Frame);
            }
        }

        public void Dispose()
        {
            Owner.Dispose();
            Other.Dispose();
            Server.Dispose();
        }
    }
}
