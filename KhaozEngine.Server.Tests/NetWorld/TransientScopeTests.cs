using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// #668: a <see cref="Transient"/> mark carries a <see cref="TransientScope"/>, so "never saved" and "gone on an
/// unload" stopped being one answer. Driven over a real <see cref="ShardedWorldServer"/> with real
/// <see cref="CellPersistence"/> and <see cref="CellEvictor"/> behind it, because the bug lived in the seam between
/// them: the evictor cached <c>SnapshotCell</c>, which is the DURABLE capture, so the one mark that kept an entity
/// out of the save also destroyed it on an unload.
/// <para>The case this was filed for is whole-zone agent state: a spawner holding one record per authored creature
/// keyed to a net id, which goes dormant while its cell is unloaded and expects its entity back on the restore, yet
/// must be re-spawned from authored content after a restart rather than resurrected out of a blob
/// (https://github.com/APKiwiOrg/Ruinborne/issues/455).</para>
/// </summary>
public class TransientScopeTests
{
    private static float Flat(float x, float z) => 0f;

    // CellSize 10, player spawn (5,_,5) -> the player's home cell is (0,0), pinned and never evictable. Everything
    // below puts its entities in (1,0) at x 15, the neighbouring cell, which is.
    private static readonly CellCoord Home = new(0, 0);
    private static readonly CellCoord Next = new(1, 0);
    private static readonly CellCoord Empty = new(3, 0);

    private static ShardedWorldServerConfig Cfg() => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),
    };

    /// <summary>A server with a joined player, persistence and eviction wired exactly as a game wires them.</summary>
    private sealed class Rig
    {
        public Rig()
        {
            (INetTransport serverTransport, INetTransport clientTransport) = LoopbackTransport.CreatePair();
            Config = Cfg();
            Store = new InMemoryWorldStore();
            Server = new ShardedWorldServer(serverTransport, Config, Flat, MoveTuning.Default);
            Persistence = new CellPersistence(Server, Store);
            Evictor = new CellEvictor(Server, Persistence);
            Client = new NetClient(clientTransport, TestHandshake.Wire(Encoding.UTF8.GetBytes("acct-1")));
            Pump(60);
            Assert.True(Server.TryGetPlayerNetId(Client.Slot, out _));
        }

        public ShardedWorldServerConfig Config { get; }
        public InMemoryWorldStore Store { get; }
        public ShardedWorldServer Server { get; }
        public CellPersistence Persistence { get; }
        public CellEvictor Evictor { get; }
        public NetClient Client { get; }

        public void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Client.Poll();
                Server.Poll();
                Server.Tick(Config.TickSeconds);
            }
        }

        // Drives one eviction to completion: request, let the store write land, then the server-thread finalize pass.
        public async Task EvictAsync(CellCoord coord)
        {
            Assert.True(Evictor.RequestEvict(coord));
            await Persistence.FlushAsync();
            Evictor.Update(0f);
        }

        /// <summary>The bytes an empty cell saves, for comparing a blob against "this coordinate held nothing". The
        /// envelope (magic + schema version + body) is coord-independent, so identical bytes mean identical content.
        /// </summary>
        public async Task<byte[]?> EmptyCellBlobAsync()
        {
            Server.EnsureCell(Empty);
            await EvictAsync(Empty);
            return await Store.LoadAsync("cell:3:0");
        }
    }

    [Fact]
    public async Task ADurableOnlyEntitySurvivesAnUnloadAndIsAbsentFromTheSave()
    {
        // The wolf case. Marked DurableOnly, so the save never hears of it and the unload freeze keeps it: the
        // coordinate comes back holding the SAME net id, which is the only handle the dormant spawner still has.
        var rig = new Rig();
        long netId = rig.Server.SpawnEntity(15f, 5f);
        rig.Pump(4);
        Assert.True(rig.Server.MarkTransient(netId, TransientScope.DurableOnly));

        await rig.EvictAsync(Next);
        Assert.Equal(1, rig.Evictor.EvictedCount);
        Assert.False(rig.Server.TryGetEntity(netId, out World _, out Entity _));   // the cell is genuinely gone

        // Never saved: the coordinate's blob is byte-for-byte the blob of a cell that held nothing.
        Assert.Equal(await rig.EmptyCellBlobAsync(), await rig.Store.LoadAsync("cell:1:0"));

        // Re-entered the way a handoff or a spawn re-enters it: the cached freeze restores inside the create call.
        rig.Server.Host.EnsureCell(Next);
        Assert.Equal(1, rig.Evictor.RestoredFromCacheCount);
        Assert.True(rig.Server.TryGetEntity(netId, out World world, out Entity entity));
        Assert.True(world.TryGet(entity, out ReplicatedPosition pos));
        Assert.Equal(15f, pos.Value.X, 3);

        // And it is still DurableOnly on the far side. The freeze bytes cannot carry an unregistered marker, so the
        // marks ride beside them: without that carry the entity comes back persistable and the next interval save
        // writes exactly the husk the mark exists to prevent.
        Assert.True(rig.Server.TryGetTransientScope(netId, out TransientScope scope));
        Assert.Equal(TransientScope.DurableOnly, scope);
        ICellPersistenceHost host = rig.Server;
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));   // still out of the save after the trip
    }

    [Fact]
    public async Task TheDefaultScopeStillEvaporatesOnBothTheSaveAndTheUnload()
    {
        // The #326 grain, unchanged: what a pickup gets, and what the old one-argument MarkTransient still means.
        var rig = new Rig();
        long netId = rig.Server.SpawnEntity(15f, 5f);
        rig.Pump(4);
        Assert.True(rig.Server.MarkTransient(netId));

        await rig.EvictAsync(Next);
        Assert.Equal(await rig.EmptyCellBlobAsync(), await rig.Store.LoadAsync("cell:1:0"));

        rig.Server.Host.EnsureCell(Next);
        Assert.Equal(1, rig.Evictor.RestoredFromCacheCount);   // the cell restored, it just carried nothing
        Assert.False(rig.Server.TryGetEntity(netId, out World _, out Entity _));
    }

    [Fact]
    public async Task AnUnmarkedEntityIsInBothCaptures()
    {
        // The control: nothing about the split moved the ordinary entity, which is saved AND comes back.
        var rig = new Rig();
        long netId = rig.Server.SpawnEntity(15f, 5f);
        rig.Pump(4);

        await rig.EvictAsync(Next);
        Assert.NotEqual(await rig.EmptyCellBlobAsync(), await rig.Store.LoadAsync("cell:1:0"));

        rig.Server.Host.EnsureCell(Next);
        Assert.True(rig.Server.TryGetEntity(netId, out World _, out Entity _));
        Assert.False(rig.Server.IsTransient(netId));
        Assert.False(rig.Server.TryGetTransientScope(netId, out TransientScope scope));
        Assert.Equal(TransientScope.Always, scope);            // a false is "not transient", not a scope
    }

    [Fact]
    public void TheOldMarkTransientOverloadIsTheAlwaysScope()
    {
        // The additive contract: an existing call site means exactly what it meant in 17.38.0.
        var rig = new Rig();
        long netId = rig.Server.SpawnEntity(15f, 5f);
        rig.Pump(2);

        Assert.True(rig.Server.MarkTransient(netId));
        Assert.True(rig.Server.IsTransient(netId));
        Assert.True(rig.Server.TryGetTransientScope(netId, out TransientScope scope));
        Assert.Equal(TransientScope.Always, scope);

        // Re-marking moves the scope rather than adding a second mark, and IsTransient stays true for both.
        Assert.True(rig.Server.MarkTransient(netId, TransientScope.DurableOnly));
        Assert.True(rig.Server.IsTransient(netId));
        Assert.True(rig.Server.TryGetTransientScope(netId, out scope));
        Assert.Equal(TransientScope.DurableOnly, scope);

        // Clearing still clears whatever scope was on it.
        Assert.True(rig.Server.ClearTransient(netId));
        Assert.False(rig.Server.IsTransient(netId));
        Assert.False(rig.Server.TryGetTransientScope(netId, out _));

        Assert.False(rig.Server.MarkTransient(999_999L, TransientScope.DurableOnly));
        Assert.False(rig.Server.TryGetTransientScope(999_999L, out _));
        Assert.True(rig.Server.TryGetPlayerNetId(rig.Client.Slot, out long playerNetId));
        Assert.False(rig.Server.MarkTransient(playerNetId, TransientScope.DurableOnly));
    }

    [Fact]
    public void BothScopesAreOutOfTheDurableCaptureTheHostHandsPersistence()
    {
        // The seam CellPersistence reads. Whatever the scope, the save never sees the entity, which is the half of
        // #326 that did not change.
        var rig = new Rig();
        long always = rig.Server.SpawnEntity(15f, 5f);
        long durableOnly = rig.Server.SpawnEntity(16f, 5f);
        rig.Pump(4);

        ICellPersistenceHost host = rig.Server;
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));

        Assert.True(rig.Server.MarkTransient(always, TransientScope.Always));
        Assert.True(rig.Server.MarkTransient(durableOnly, TransientScope.DurableOnly));

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));                             // entity count 0
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next, SnapshotPurpose.Durable));
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next, SnapshotPurpose.Eviction)); // holds one
    }

    [Fact]
    public void TheMarksRideBesideTheCaptureBecauseTheBytesCannotCarryThem()
    {
        // The seam that makes the route back honest, read directly. ReadTransientMarks names exactly the entities an
        // eviction capture KEEPS but cannot encode, and the durable purpose has nothing to carry because it keeps
        // none of them.
        var rig = new Rig();
        long always = rig.Server.SpawnEntity(15f, 5f);
        long durableOnly = rig.Server.SpawnEntity(16f, 5f);
        long plain = rig.Server.SpawnEntity(17f, 5f);
        rig.Pump(4);
        Assert.True(rig.Server.MarkTransient(always));
        Assert.True(rig.Server.MarkTransient(durableOnly, TransientScope.DurableOnly));

        ICellPersistenceHost host = rig.Server;
        Assert.Empty(host.ReadTransientMarks(Next, SnapshotPurpose.Durable));

        System.Collections.Generic.IReadOnlyDictionary<long, TransientScope> marks =
            host.ReadTransientMarks(Next, SnapshotPurpose.Eviction);
        Assert.Equal(TransientScope.DurableOnly, Assert.Contains(durableOnly, marks));
        Assert.DoesNotContain(always, marks);      // not in the capture at all, so nothing to re-mark
        Assert.DoesNotContain(plain, marks);
    }

    [Fact]
    public void AHostThatDidNotOverrideThePurposeOverloadKeepsTheShippedBehaviour()
    {
        // The default interface method is the back-compat promise: adding a purpose to ICellPersistenceHost cannot
        // change what an existing consumer implementation returns, so it stays on the 17.38.0 grain until it opts in.
        var host = new LegacyHost();
        ICellPersistenceHost seam = host;
        Assert.Equal(new byte[] { 7 }, seam.SnapshotCell(Home));
        Assert.Equal(new byte[] { 7 }, seam.SnapshotCell(Home, SnapshotPurpose.Eviction));
        Assert.Equal(2, host.DurableCalls);   // both asks landed on the one durable implementation
        Assert.Empty(seam.ReadTransientMarks(Home, SnapshotPurpose.Eviction));
        seam.ApplyTransientMarks(Home, new System.Collections.Generic.Dictionary<long, TransientScope> { [1L] = TransientScope.DurableOnly });
    }

    /// <summary>A host written before the purpose overload existed: it implements the one-argument member only.</summary>
    private sealed class LegacyHost : ICellPersistenceHost
    {
        public int DurableCalls { get; private set; }

#pragma warning disable CS0067   // the seam requires the event; this host never raises one
        public event System.Action<CellCoord>? CellCreated;
#pragma warning restore CS0067

        public System.Collections.Generic.IReadOnlyCollection<CellCoord> LiveCellCoords => System.Array.Empty<CellCoord>();

        public byte[]? SnapshotCell(CellCoord coord)
        {
            DurableCalls++;
            return new byte[] { 7 };
        }

        public System.Collections.Generic.IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot) =>
            System.Array.Empty<long>();

        public void EnsureCell(CellCoord coord) { }
        public long NextNetId => 1;
        public void EnsureNextNetIdAtLeast(long atLeast) { }
    }
}
