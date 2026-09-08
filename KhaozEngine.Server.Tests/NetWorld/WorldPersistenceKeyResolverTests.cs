using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldPersistenceKeyResolverTests
{
    private const float Dt = 1f / 30f;

    private sealed class ClaimAuthenticator : IConnectionAuthenticator, IConnectionPersistenceKey
    {
        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            string[] claims = Encoding.UTF8.GetString(token).Split('\n', 2);
            subject = claims[0];
            rejectReason = string.Empty;
            return true;
        }

        public string ReadPersistenceKey(ReadOnlySpan<byte> token)
        {
            string[] claims = Encoding.UTF8.GetString(token).Split('\n', 2);
            return claims.Length == 2 ? claims[1] : string.Empty;
        }
    }

    private sealed class RecordingHost : IWorldPersistenceHost
    {
        private readonly Dictionary<int, string> accounts = new();
        private readonly Dictionary<int, string> persistenceKeys = new();
        private readonly Dictionary<int, PlayerMoveState> states = new();
        private Action<int, string>? joined;
        private Action<int, string, PlayerMoveState>? leaving;
        private PersistenceKeyResolver? resolver;

        public List<string> InstallationOrder { get; } = new();
        public List<(int Slot, PlayerMoveState State, bool Teleport)> Placements { get; } = new();

        public event Action<int, string>? PlayerJoined
        {
            add { InstallationOrder.Add("joined"); joined += value; }
            remove => joined -= value;
        }

        public event Action<int, string, PlayerMoveState>? PlayerLeaving
        {
            add { InstallationOrder.Add("leaving"); leaving += value; }
            remove => leaving -= value;
        }

        public bool TrySetPersistenceKeyResolver(PersistenceKeyResolver? value)
        {
            InstallationOrder.Add("resolver");
            resolver = value;
            return true;
        }

        public void SetResumePositionProvider(ResumePositionProvider? provider) =>
            InstallationOrder.Add("hint");

        public IReadOnlyCollection<int> JoinedSlots => accounts.Keys;

        public bool TryGetAccountId(int slot, out string accountId) =>
            accounts.TryGetValue(slot, out accountId!);

        public bool TryGetPersistenceKey(int slot, out string persistenceKey) =>
            persistenceKeys.TryGetValue(slot, out persistenceKey!);

        public bool TryGetPlayerState(int slot, out PlayerMoveState state) =>
            states.TryGetValue(slot, out state);

        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
        {
            states[slot] = state;
            Placements.Add((slot, state, teleport));
        }

        public void Join(int slot, string accountId, string verifiedPersistenceKey, PlayerMoveState state)
        {
            accounts[slot] = accountId;
            states[slot] = state;
            string key = resolver is null
                ? accountId
                : resolver(new PersistenceKeyRequest(slot, accountId, verifiedPersistenceKey));
            persistenceKeys[slot] = key;
            joined?.Invoke(slot, accountId);
        }

        public void ReplaceSubject(int slot, string accountId) => accounts[slot] = accountId;

        public void Leave(int slot)
        {
            if (!accounts.TryGetValue(slot, out string? accountId) || !states.TryGetValue(slot, out PlayerMoveState state))
                return;
            leaving?.Invoke(slot, accountId, state);
            persistenceKeys.Remove(slot);
            accounts.Remove(slot);
            states.Remove(slot);
        }
    }

    private sealed class LegacyHost : IWorldPersistenceHost
    {
        private readonly Dictionary<int, (string AccountId, PlayerMoveState State)> players = new();
        public event Action<int, string>? PlayerJoined;
        public event Action<int, string, PlayerMoveState>? PlayerLeaving;
        public IReadOnlyCollection<int> JoinedSlots => players.Keys;

        public bool TryGetAccountId(int slot, out string accountId)
        {
            if (players.TryGetValue(slot, out (string AccountId, PlayerMoveState State) player))
            {
                accountId = player.AccountId;
                return true;
            }
            accountId = string.Empty;
            return false;
        }

        public bool TryGetPlayerState(int slot, out PlayerMoveState state)
        {
            if (players.TryGetValue(slot, out (string AccountId, PlayerMoveState State) player))
            {
                state = player.State;
                return true;
            }
            state = default;
            return false;
        }

        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
        {
            if (players.TryGetValue(slot, out (string AccountId, PlayerMoveState State) player))
                players[slot] = (player.AccountId, state);
        }

        public void Join(int slot, string accountId, PlayerMoveState state)
        {
            players[slot] = (accountId, state);
            PlayerJoined?.Invoke(slot, accountId);
        }

        public void Leave(int slot)
        {
            if (!players.TryGetValue(slot, out (string AccountId, PlayerMoveState State) player)) return;
            PlayerLeaving?.Invoke(slot, player.AccountId, player.State);
            players.Remove(slot);
        }
    }

    [Fact]
    public void ResolverIsInstalledBeforeSubscriptionsAndResumeHints()
    {
        var host = new RecordingHost();

        _ = new WorldPersistence(host, new InMemoryWorldStore(), new WorldPersistenceConfig
        {
            PersistenceKeyResolver = (in PersistenceKeyRequest request) => request.VerifiedPersistenceKey,
        });

        Assert.Equal(new[] { "resolver", "joined", "leaving", "hint" }, host.InstallationOrder);
    }

    [Fact]
    public async Task LegacyHostAndNullResolverKeepAccountKeyBehavior()
    {
        var store = new InMemoryWorldStore();
        var host = new LegacyHost();
        var persistence = new WorldPersistence(host, store);

        host.Join(3, "acct:3", new PlayerMoveState { Position = new Vector3(3f, 0f, 4f) });
        await persistence.FlushAsync();
        host.Leave(3);
        await persistence.FlushAsync();

        Assert.NotNull(await store.LoadAsync("player:acct:3"));
    }

    [Fact]
    public async Task DurablePathsAndGameHooksUseBoundKeyWhileContextKeepsSubject()
    {
        var store = new InMemoryWorldStore();
        byte[] blob = Encoding.UTF8.GetBytes("xp=4");
        await store.SaveAsync("player:character:9", PlayerRecord.From(
            new PlayerMoveState { Position = new Vector3(9f, 0f, 2f) }, blob).Encode());
        var contexts = new List<PlayerPersistenceContext>();
        var host = new RecordingHost();
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig
        {
            PersistenceKeyResolver = (in PersistenceKeyRequest request) => request.VerifiedPersistenceKey,
            CaptureGameState = (in PlayerPersistenceContext context) =>
            {
                contexts.Add(context);
                return blob;
            },
            ApplyGameState = (in PlayerPersistenceContext context, ReadOnlySpan<byte> _) => contexts.Add(context),
        });

        host.Join(4, "acct:4", "character:9", new PlayerMoveState());
        await persistence.FlushAsync();
        Assert.Equal(new Vector3(9f, 0f, 2f), Assert.Single(host.Placements).State.Position);

        host.Leave(4);
        await persistence.FlushAsync();

        Assert.All(contexts, context =>
        {
            Assert.Equal("character:9", context.AccountId);
            Assert.Equal("acct:4", context.AuthenticatedAccountId);
        });
        Assert.NotNull(await store.LoadAsync("player:character:9"));
        Assert.Null(await store.LoadAsync("player:acct:4"));
        Assert.True(persistence.ResumeHints.TryGet("character:9", out _));
        Assert.False(persistence.ResumeHints.TryGet("acct:4", out _));
    }

    [Fact]
    public async Task PendingApplyAlsoRequiresTheAuthenticatedSubjectToMatch()
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:character:9", PlayerRecord.From(
            new PlayerMoveState { Position = new Vector3(9f, 0f, 2f) }).Encode());
        var store = new GatedWorldStore(inner);
        var host = new RecordingHost();
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig
        {
            PersistenceKeyResolver = (in PersistenceKeyRequest request) => request.VerifiedPersistenceKey,
        });
        var dropped = new List<(string AccountId, int Slot)>();
        persistence.OnLoadApplyDropped += (accountId, slot) => dropped.Add((accountId, slot));

        host.Join(4, "acct:4", "character:9", new PlayerMoveState());
        Assert.Equal(1, store.PendingLoads);
        host.ReplaceSubject(4, "acct:other");
        store.ReleaseLoads();
        await persistence.FlushAsync();

        Assert.Empty(host.Placements);
        Assert.Equal(new[] { ("character:9", 4) }, dropped);
    }

    [Fact]
    public async Task QuarantineUsesTheBoundPersistenceKey()
    {
        var store = new InMemoryWorldStore();
        byte[] corrupt = Encoding.UTF8.GetBytes("not a player record");
        await store.SaveAsync("player:character:9", corrupt);
        var host = new RecordingHost();
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig
        {
            PersistenceKeyResolver = (in PersistenceKeyRequest request) => request.VerifiedPersistenceKey,
        });
        string? quarantined = null;
        persistence.OnRecordQuarantined += (key, _) => quarantined = key;

        host.Join(4, "acct:4", "character:9", new PlayerMoveState());
        await persistence.FlushAsync();

        Assert.Equal("character:9", quarantined);
        Assert.Equal(corrupt, await store.LoadAsync("quarantine:player:character:9"));
        Assert.Null(await store.LoadAsync("quarantine:player:acct:4"));
    }

    [Fact]
    public async Task PrewarmTreatsStoredSuffixAsTheDurablePersistenceKey()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:character:9", PlayerRecord.From(
            new PlayerMoveState { Position = new Vector3(9f, 0f, 2f) }).Encode());
        var persistence = new WorldPersistence(new LegacyHost(), store);

        int count = await persistence.PrewarmResumeHintsAsync();

        Assert.Equal(1, count);
        Assert.True(persistence.ResumeHints.TryGet("character:9", out Vector3 position));
        Assert.Equal(new Vector3(9f, 0f, 2f), position);
        Assert.False(persistence.ResumeHints.TryGet("acct:4", out _));
    }

    [Fact]
    public async Task DiagnosticsNameBothIdentitiesWhenTheyDiffer()
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:character:9", PlayerRecord.From(
            new PlayerMoveState { Position = new Vector3(9f, 0f, 2f) }).Encode());
        var store = new GatedWorldStore(inner);
        var host = new RecordingHost();
        var diagnostics = new List<string>();
        var persistence = new StatePersistence<PlayerMoveState>(host, store, new PersistenceBinding<PlayerMoveState>(
            PositionOf: state => state.Position,
            Encode: (state, game) => PlayerRecord.From(state, game).Encode(),
            Decode: (byte[] data, out PlayerMoveState state, out byte[]? game) =>
            {
                PlayerRecord record = PlayerRecord.Decode(data);
                state = record.ToState();
                game = record.Game;
                return true;
            },
            Validate: (_, _) => null), new PersistenceCoreConfig
            {
                PersistenceKeyResolver = (in PersistenceKeyRequest request) => request.VerifiedPersistenceKey,
                Diagnostic = (message, _) => diagnostics.Add(message),
            });

        host.Join(4, "acct:4", "character:9", new PlayerMoveState());
        host.ReplaceSubject(4, "acct:other");
        store.ReleaseLoads();
        await persistence.FlushAsync();

        string message = Assert.Single(diagnostics);
        Assert.Contains("acct:4", message, StringComparison.Ordinal);
        Assert.Contains("character:9", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoArgumentPersistenceContextUsesOneIdentityForBothProperties()
    {
        var context = new PlayerPersistenceContext(7, "acct:7");

        Assert.Equal("acct:7", context.AccountId);
        Assert.Equal("acct:7", context.AuthenticatedAccountId);
    }

    [Fact]
    public async Task TokenlessGameStateContextHasNoAuthenticatedSubject()
    {
        var host = new LegacyHost();
        PlayerPersistenceContext? captured = null;
        var persistence = new WorldPersistence(host, new InMemoryWorldStore(), new WorldPersistenceConfig
        {
            PersistGuests = true,
            CaptureGameState = (in PlayerPersistenceContext context) =>
            {
                captured = context;
                return null;
            },
        });
        host.Join(7, "guest:7", new PlayerMoveState());
        host.Leave(7);
        await persistence.FlushAsync();

        Assert.NotNull(captured);
        Assert.StartsWith("guest:", captured.Value.AccountId, StringComparison.Ordinal);
        Assert.Equal(string.Empty, captured.Value.AuthenticatedAccountId);
    }

    private sealed class HeadRig : IDisposable
    {
        private readonly Action step;
        private readonly IDisposable? disposableHost;
        public InMemoryTransportHub Hub { get; }
        public IWorldPersistenceHost Host { get; }
        public WorldPersistence Persistence { get; }
        public List<PersistenceKeyRequest> Requests { get; } = new();
        public List<NetClient> Clients { get; } = new();
        public List<INetTransport> ClientTransports { get; } = new();

        public HeadRig(bool sharded, PersistenceKeyResolver? resolver = null,
            DuplicateSessionPolicy duplicateSessions = DuplicateSessionPolicy.KickOlder)
        {
            Hub = new InMemoryTransportHub();
            var authenticator = new ClaimAuthenticator();
            if (sharded)
            {
                var server = new ShardedWorldServer(Hub.Server, new ShardedWorldServerConfig
                {
                    TickSeconds = Dt,
                    MaxPlayers = 8,
                    CellSize = 60f,
                    OverlapMargin = 24f,
                    InterestRadius = 24f,
                    SpawnPosition = _ => Vector3.Zero,
                    DuplicateSessions = duplicateSessions,
                }, static (_, _) => 0f, MoveTuning.Default, authenticator: authenticator);
                Host = server;
                disposableHost = server;
                step = () => { server.Poll(); server.Tick(Dt); };
            }
            else
            {
                var server = new WorldServer(Hub.Server, new WorldServerConfig
                {
                    TickSeconds = Dt,
                    MaxPlayers = 8,
                    SpawnPosition = _ => Vector3.Zero,
                    DuplicateSessions = duplicateSessions,
                }, static (_, _) => 0f, MoveTuning.Default, authenticator: authenticator);
                Host = server;
                step = () => { server.Poll(); server.Tick(Dt); };
            }

            PersistenceKeyResolver effective = (in PersistenceKeyRequest request) =>
            {
                Requests.Add(request);
                return resolver is null ? request.VerifiedPersistenceKey : resolver(request);
            };
            Persistence = new WorldPersistence(Host, new InMemoryWorldStore(), new WorldPersistenceConfig
            {
                SaveIntervalSeconds = 999f,
                PersistenceKeyResolver = effective,
            });
        }

        public NetClient Connect(string accountId, string persistenceKey)
        {
            INetTransport transport = Hub.CreateClient();
            string token = accountId + "\n" + persistenceKey;
            var client = new NetClient(transport, TestHandshake.Wire(Encoding.UTF8.GetBytes(token)));
            ClientTransports.Add(transport);
            Clients.Add(client);
            return client;
        }

        public NetClient ConnectGuest()
        {
            INetTransport transport = Hub.CreateClient();
            var client = new NetClient(transport, TestHandshake.Wire());
            ClientTransports.Add(transport);
            Clients.Add(client);
            return client;
        }

        public void Pump(Func<bool> until, int frames = 200)
        {
            for (int i = 0; i < frames && !until(); i++)
            {
                foreach (NetClient client in Clients) client.Poll();
                step();
                Persistence.Update(Dt);
            }
        }

        public void Drop(NetClient client) => Hub.DisconnectClient(ClientTransports[Clients.IndexOf(client)]);

        public void Dispose()
        {
            disposableHost?.Dispose();
            Hub.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothHeadsBindTheResolvedKeyBeforeSpawnAndExposeItThroughJoin(bool sharded)
    {
        using var rig = new HeadRig(sharded);
        var joinedKeys = new List<string>();
        rig.Persistence.ResumeHints.Record("character:9", new Vector3(9f, 0f, 2f));
        rig.Host.PlayerJoined += (slot, _) =>
        {
            Assert.True(rig.Host.TryGetPersistenceKey(slot, out string key));
            joinedKeys.Add(key);
        };

        NetClient client = rig.Connect("acct:4", "character:9");
        rig.Pump(() => client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);

        PersistenceKeyRequest request = Assert.Single(rig.Requests);
        Assert.Equal(client.Slot, request.Slot);
        Assert.Equal("acct:4", request.AuthenticatedAccountId);
        Assert.Equal("character:9", request.VerifiedPersistenceKey);
        Assert.Equal(new[] { "character:9" }, joinedKeys);
        Assert.True(rig.Host.TryGetAccountId(client.Slot, out string accountId));
        Assert.Equal("acct:4", accountId);
        Assert.True(rig.Host.TryGetPlayerState(client.Slot, out PlayerMoveState state));
        Assert.Equal(9f, state.Position.X, 3);
        Assert.Equal(2f, state.Position.Z, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothHeadsKeepTheBoundKeyThroughLeavingThenRemoveIt(bool sharded)
    {
        using var rig = new HeadRig(sharded);
        string? leavingKey = null;
        rig.Host.PlayerLeaving += (slot, _, _) =>
        {
            Assert.True(rig.Host.TryGetPersistenceKey(slot, out string key));
            leavingKey = key;
        };
        NetClient client = rig.Connect("acct:4", "character:9");
        rig.Pump(() => rig.Host.JoinedSlots.Count == 1);
        int slot = client.Slot;

        rig.Drop(client);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);

        Assert.Equal("character:9", leavingKey);
        Assert.Single(rig.Requests);
        Assert.False(rig.Host.TryGetPersistenceKey(slot, out _));
        Assert.Single(rig.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecycledSlotReceivesOnlyItsNewPersistenceKey(bool sharded)
    {
        using var rig = new HeadRig(sharded);
        NetClient first = rig.Connect("acct:first", "character:first");
        rig.Pump(() => rig.Host.JoinedSlots.Count == 1 && first.Slot >= 0);
        int recycled = first.Slot;
        rig.Drop(first);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);

        NetClient second = rig.Connect("acct:second", "character:second");
        rig.Pump(() => rig.Host.JoinedSlots.Count == 1 && second.Slot >= 0);

        Assert.Equal(recycled, second.Slot);
        Assert.True(rig.Host.TryGetPersistenceKey(recycled, out string key));
        Assert.Equal("character:second", key);
        Assert.Equal(2, rig.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentSubjectsCannotShareOneLivePersistenceKey(bool sharded)
    {
        using var rig = new HeadRig(sharded);
        NetClient first = rig.Connect("acct:first", "character:shared");
        rig.Pump(() => rig.Host.JoinedSlots.Count == 1);
        NetClient second = rig.Connect("acct:second", "character:shared");
        rig.Pump(() => rig.Requests.Count == 2);
        rig.Pump(() => false, 4);

        int slot = Assert.Single(rig.Host.JoinedSlots);
        Assert.Equal(first.Slot, slot);
        Assert.True(rig.Host.TryGetAccountId(slot, out string accountId));
        Assert.Equal("acct:first", accountId);
        Assert.True(rig.Host.TryGetPersistenceKey(slot, out string persistenceKey));
        Assert.Equal("character:shared", persistenceKey);
        Assert.Equal(2, rig.Requests.Count);
        Assert.True(second.Slot < 0 || second.Slot != slot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestsBypassTheResolver(bool sharded)
    {
        using var rig = new HeadRig(sharded);
        NetClient guest = rig.ConnectGuest();

        rig.Pump(() => rig.Host.JoinedSlots.Count == 1 && guest.Slot >= 0);

        Assert.Empty(rig.Requests);
        Assert.True(rig.Host.TryGetAccountId(guest.Slot, out string accountId));
        Assert.Equal("guest:" + guest.Slot, accountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateSessionPolicyStillUsesTheAuthenticatedSubject(bool sharded)
    {
        using var rig = new HeadRig(sharded, duplicateSessions: DuplicateSessionPolicy.RefuseNewer);
        NetClient first = rig.Connect("acct:same", "character:first");
        rig.Pump(() => rig.Host.JoinedSlots.Count == 1 && first.Slot >= 0);
        NetClient second = rig.Connect("acct:same", "character:second");
        rig.Pump(() => rig.Requests.Count > 1 || second.Slot >= 0, 20);

        int slot = Assert.Single(rig.Host.JoinedSlots);
        Assert.Equal(first.Slot, slot);
        Assert.Single(rig.Requests);
        Assert.Equal("character:first", rig.Requests[0].VerifiedPersistenceKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubjectHandoverReleasesTheOldBindingBeforeTheNewOneBinds(bool sharded)
    {
        using var rig = new HeadRig(sharded);
        var leavingKeys = new List<string>();
        rig.Host.PlayerLeaving += (slot, _, _) =>
        {
            Assert.True(rig.Host.TryGetPersistenceKey(slot, out string key));
            leavingKeys.Add(key);
        };
        NetClient first = rig.Connect("acct:same", "character:9");
        rig.Pump(() => rig.Host.JoinedSlots.Count == 1);
        NetClient second = rig.Connect("acct:same", "character:9");

        rig.Pump(() => rig.Requests.Count == 2 && second.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);

        Assert.Equal(new[] { "character:9" }, leavingKeys);
        Assert.Equal(2, rig.Requests.Count);
        int slot = Assert.Single(rig.Host.JoinedSlots);
        Assert.True(rig.Host.TryGetPersistenceKey(slot, out string key));
        Assert.Equal("character:9", key);
        Assert.True(first.Slot < 0 || first.Slot != slot);
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(false, "guest")]
    [InlineData(false, "long")]
    [InlineData(false, "throw")]
    [InlineData(true, "empty")]
    [InlineData(true, "guest")]
    [InlineData(true, "long")]
    [InlineData(true, "throw")]
    public void InvalidResolverResultsAreRejectedBeforeSpawn(bool sharded, string kind)
    {
        PersistenceKeyResolver resolver = kind switch
        {
            "empty" => (in PersistenceKeyRequest _) => string.Empty,
            "guest" => (in PersistenceKeyRequest _) => "guest:forbidden",
            "long" => (in PersistenceKeyRequest _) => new string('x', 444),
            _ => (in PersistenceKeyRequest _) => throw new InvalidOperationException("resolver failed"),
        };
        using var rig = new HeadRig(sharded, resolver);
        _ = rig.Connect("acct:4", "character:9");

        rig.Pump(() => rig.Requests.Count == 1);
        rig.Pump(() => false, 4);

        Assert.Empty(rig.Host.JoinedSlots);
        Assert.Single(rig.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondNonNullResolverCannotBeInstalled(bool sharded)
    {
        using var rig = new HeadRig(sharded);

        Assert.Throws<InvalidOperationException>(() => new WorldPersistence(
            rig.Host, new InMemoryWorldStore(), new WorldPersistenceConfig
            {
                PersistenceKeyResolver = (in PersistenceKeyRequest request) => request.VerifiedPersistenceKey,
            }));
    }
}
