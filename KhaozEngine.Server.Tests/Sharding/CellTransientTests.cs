using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// The per-entity persist opt-out (#326): a <see cref="Transient"/> entity is absent from
/// <see cref="CellSim.SnapshotOwned(System.Collections.Generic.IReadOnlySet{long})"/>'s blob rather than
/// present with fewer components, so no restore can bring it back as a husk. Pins the three things that make the marker safe to reach for: the blob is byte-identical to one
/// taken with the entity never spawned at all, the mark reaches no wire, and it follows the entity across a cell
/// handoff so a crossing does not quietly make it persistable again. The handoff coverage is per link SHAPE, not
/// per call: a link that delivers the Migrate on a later <see cref="ShardHost.ProcessHandoffs"/> call, which is what
/// the <see cref="ICellLink"/> network-impl contract describes, re-marks on arrival too.
/// </summary>
public class CellTransientTests
{
    private struct Blob : IComponent { public int V; }
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Blob>(typeId: 1, write: (b, bw) => bw.Write(b.V), read: br => new Blob { V = br.ReadInt32() });
        r.Register<Pos>(typeId: 2,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static CellSim Cell(ReplicationRegistry r) => new(new CellCoord(0, 0), 1f / 30f, r, 10f);

    private static Entity Owned(CellSim c, int netId, int v)
    {
        Entity e = c.World.Spawn();
        c.World.Set(e, new NetId(netId));
        c.World.Set(e, new Blob { V = v });
        c.RegisterOwned(netId, e);
        return e;
    }

    /// <summary>
    /// The networked link shape <see cref="ICellLink"/>'s contract describes: a Migrate is STAGED on send and only
    /// becomes drainable after <see cref="DeliverStaged"/>, so a crossing spans <see cref="ShardHost.ProcessHandoffs"/>
    /// calls instead of completing inside one. Everything else (acks, ghost sync) is delivered in process as usual.
    /// </summary>
    private sealed class DeferringMigrateLink : ICellLink
    {
        private readonly InProcessCellLink inner = new();
        private readonly List<CellMessage> staged = new();

        public void Send(in CellMessage message)
        {
            if (message.Kind == CellMessageKind.Migrate) staged.Add(message);
            else inner.Send(message);
        }

        public IReadOnlyList<CellMessage> Drain(CellCoord target, CellMessageKind kind) => inner.Drain(target, kind);

        public bool HasPending(CellCoord target) =>
            inner.HasPending(target) || staged.Exists(m => m.Target == target);

        public void Forget(CellCoord target) => inner.Forget(target);

        /// <summary>Hands over everything staged so far, as a node does once the wire hop completed.</summary>
        public void DeliverStaged()
        {
            foreach (CellMessage m in staged) inner.Send(m);
            staged.Clear();
        }
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    [Fact]
    public void SnapshotOwned_LeavesATransientEntityOutOfTheBlob()
    {
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Owned(c, 5, 50);                                          // persistable
        Entity transient = Owned(c, 9, 90);
        c.World.Set(transient, default(Transient));

        byte[] snap = c.SnapshotOwned(new HashSet<long>());

        CellSim restored = Cell(r);
        IReadOnlyList<long> ids = restored.RestoreOwned(snap);
        Assert.Equal(new long[] { 5 }, ids);                      // only the unmarked one came back
        Assert.False(restored.TryGetOwned(9, out Entity _));
        Assert.True(restored.TryGetOwned(5, out Entity e));
        Assert.True(restored.World.TryGet(e, out Blob b));
        Assert.Equal(50, b.V);
    }

    [Fact]
    public void ATransientEntityIsAbsentFromTheBytes_NotAStrippedHusk()
    {
        // The distinction the marker exists for. A per-component channel flag would have left the entity in the blob
        // with no components, which restores as a husk. Absent means the bytes are the bytes of a cell that never
        // held it, so there is nothing for a restore to rebuild.
        ReplicationRegistry r = Registry();
        CellSim withTransient = Cell(r);
        Owned(withTransient, 5, 50);
        Entity extra = Owned(withTransient, 9, 90);
        withTransient.World.Set(extra, default(Transient));

        CellSim withoutIt = Cell(r);
        Owned(withoutIt, 5, 50);

        Assert.Equal(withoutIt.SnapshotOwned(new HashSet<long>()), withTransient.SnapshotOwned(new HashSet<long>()));
    }

    [Fact]
    public void ClearingTheMarkMakesTheEntityPersistableAgain()
    {
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Entity e = Owned(c, 9, 90);
        c.World.Set(e, default(Transient));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, c.SnapshotOwned(new HashSet<long>()));   // entity count 0

        c.World.Remove<Transient>(e);

        CellSim restored = Cell(r);
        Assert.Equal(new long[] { 9 }, restored.RestoreOwned(c.SnapshotOwned(new HashSet<long>())));
    }

    [Fact]
    public void TheMarkReachesNoWire()
    {
        // Persistence is a server-local decision, so the marker is in no ReplicationRegistry and spends no type id.
        // Marking an entity must therefore not move one byte of what a client (or a handoff) is sent.
        ReplicationRegistry r = Registry();
        CellSim plain = Cell(r);
        Owned(plain, 5, 50);
        CellSim marked = Cell(r);
        Entity e = Owned(marked, 5, 50);
        marked.World.Set(e, default(Transient));

        var all = new HashSet<long> { 5 };
        Assert.Equal(
            SnapshotWriter.WriteFiltered(plain.World, r, all, ReplicationChannels.Replicate, ownerNetId: null),
            SnapshotWriter.WriteFiltered(marked.World, r, all, ReplicationChannels.Replicate, ownerNetId: null));
        Assert.Equal(
            SnapshotWriter.WriteFiltered(plain.World, r, all, ReplicationChannels.Migrate, ownerNetId: null),
            SnapshotWriter.WriteFiltered(marked.World, r, all, ReplicationChannels.Migrate, ownerNetId: null));
    }

    [Fact]
    public void AHandoffCarriesTheMarkToTheDestinationCell()
    {
        // The Migrate capture cannot carry an unregistered marker, so ProcessHandoffs carries it beside the capture.
        // Without that, a transient entity walking over a border becomes persistable in its new cell.
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 20f, positionAccessor: PosAccessor);

        Entity e = host.SpawnOwned(50f, 50f, netId: 7, out CellSim source);
        source.World.Set(e, new Pos { X = 50f, Y = 50f });
        source.World.Set(e, new Blob { V = 1 });
        source.World.Set(e, default(Transient));
        Assert.Equal(new CellCoord(0, 0), source.Coord);

        source.World.Set(e, new Pos { X = 150f, Y = 50f });   // over the border into (1,0)
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim dest, out Entity moved));
        Assert.Equal(new CellCoord(1, 0), dest.Coord);
        Assert.True(dest.World.Has<Transient>(moved));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dest.SnapshotOwned(new HashSet<long>()));
    }

    [Fact]
    public void ADeferredMigrateStillArrivesMarked()
    {
        // The link shape the ICellLink contract documents and every cross-node implementation has: the Migrate is
        // delivered a call after it was sent. The mark is read in phase 1 of the FIRST call and has to still be
        // there in phase 2 of the SECOND, or the destination adopts an unmarked entity and the next interval save
        // writes exactly the husk #326 exists to prevent.
        var link = new DeferringMigrateLink();
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 20f, positionAccessor: PosAccessor, cellLink: link);

        Entity e = host.SpawnOwned(50f, 50f, netId: 7, out CellSim source);
        source.World.Set(e, new Pos { X = 50f, Y = 50f });
        source.World.Set(e, new Blob { V = 1 });
        source.World.Set(e, default(Transient));

        source.World.Set(e, new Pos { X = 150f, Y = 50f });   // over the border into (1,0)
        host.ProcessHandoffs();                               // sends the Migrate, which the link holds back
        Assert.False(host.TryGetOwner(7, out CellSim _, out Entity _));   // still in flight, nobody owns it

        link.DeliverStaged();
        host.ProcessHandoffs();                               // the destination adopts it on THIS call

        Assert.True(host.TryGetOwner(7, out CellSim dest, out Entity moved));
        Assert.Equal(new CellCoord(1, 0), dest.Coord);
        Assert.True(dest.World.Has<Transient>(moved));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dest.SnapshotOwned(new HashSet<long>()));
    }

    // ---- #668: the mark carries a scope, and both captures ask for their own ----

    [Fact]
    public void AnEvictionCaptureKeepsADurableOnlyEntityAndStillDropsAnAlwaysOne()
    {
        // The split itself, at the seam that decides it. The durable capture is unchanged (both marks are out of
        // it), and the eviction capture keeps the DurableOnly one, so an unload is a freeze rather than a second
        // persistence decision.
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Owned(c, 5, 50);                                             // plain
        Entity always = Owned(c, 8, 80);
        Entity durableOnly = Owned(c, 9, 90);
        c.World.Set(always, new Transient { Scope = TransientScope.Always });
        c.World.Set(durableOnly, new Transient { Scope = TransientScope.DurableOnly });

        CellSim fromSave = Cell(r);
        Assert.Equal(new long[] { 5 },
            fromSave.RestoreOwned(c.SnapshotOwned(new HashSet<long>(), SnapshotPurpose.Durable)));

        CellSim fromUnload = Cell(r);
        IReadOnlyList<long> back = fromUnload.RestoreOwned(c.SnapshotOwned(new HashSet<long>(), SnapshotPurpose.Eviction));
        Assert.Equal(2, back.Count);
        Assert.Contains(5L, back);
        Assert.Contains(9L, back);
        Assert.DoesNotContain(8L, back);
        Assert.True(fromUnload.TryGetOwned(9, out Entity restored));
        Assert.True(fromUnload.World.TryGet(restored, out Blob b));
        Assert.Equal(90, b.V);                                       // the same entity, components and all
    }

    [Fact]
    public void TheMarksRideBesideAnEvictionCaptureAndReApplyOnTheFarSide()
    {
        // The marker is in no registry (#326), so an eviction capture carries the ENTITY and not its mark. Without
        // the carry the far side owns an unmarked entity, which the next durable capture would happily write. The
        // same shape as ProcessHandoffs carrying a mark beside a Migrate.
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Owned(c, 5, 50);
        Entity always = Owned(c, 8, 80);
        Entity durableOnly = Owned(c, 9, 90);
        c.World.Set(always, new Transient { Scope = TransientScope.Always });
        c.World.Set(durableOnly, new Transient { Scope = TransientScope.DurableOnly });

        Assert.Empty(c.ReadTransientMarks(SnapshotPurpose.Durable));   // nothing kept, so nothing to carry
        IReadOnlyDictionary<long, TransientScope> marks = c.ReadTransientMarks(SnapshotPurpose.Eviction);
        Assert.Equal(TransientScope.DurableOnly, Assert.Contains(9L, marks));
        Assert.DoesNotContain(8L, marks);
        Assert.DoesNotContain(5L, marks);

        CellSim back = Cell(r);
        back.RestoreOwned(c.SnapshotOwned(new HashSet<long>(), SnapshotPurpose.Eviction));
        Assert.True(back.TryGetOwned(9, out Entity restored));
        Assert.False(back.World.Has<Transient>(restored));             // the bytes could not carry it

        Assert.Equal(1, back.ApplyTransientMarks(marks));
        Assert.True(back.World.TryGet(restored, out Transient mark));
        Assert.Equal(TransientScope.DurableOnly, mark.Scope);
        // And with the mark back on it, the far side's own durable capture leaves it out again.
        CellSim saved = Cell(r);
        Assert.Equal(new long[] { 5 }, saved.RestoreOwned(back.SnapshotOwned(new HashSet<long>())));
    }

    [Fact]
    public void ApplyingAMarkForANetIdTheCellDoesNotOwnIsSkipped()
    {
        // The cache-dropped case: a coordinate that fell back to the store-backed load holds none of the entities
        // the freeze did, so the carried marks must land on nothing rather than throw or invent an entity.
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Owned(c, 5, 50);

        var marks = new Dictionary<long, TransientScope> { [9L] = TransientScope.DurableOnly };
        Assert.Equal(0, c.ApplyTransientMarks(marks));
        Assert.False(c.TryGetOwned(9, out Entity _));
    }

    [Fact]
    public void TheDefaultMarkIsTheAlwaysScope()
    {
        // The additive contract at the component level: default(Transient) is what 17.38.0 shipped, so every mark
        // written before the scope existed still means "out of every capture".
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Entity e = Owned(c, 9, 90);
        c.World.Set(e, default(Transient));

        Assert.True(c.World.TryGet(e, out Transient mark));
        Assert.Equal(TransientScope.Always, mark.Scope);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, c.SnapshotOwned(new HashSet<long>()));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, c.SnapshotOwned(new HashSet<long>(), SnapshotPurpose.Eviction));
    }

    [Fact]
    public void AHandoffCarriesTheSCOPE_NotJustTheMark()
    {
        // A DurableOnly entity that walks over a border must arrive DurableOnly. Arriving as the default Always
        // would leave it persistable-never AND destroyed by the destination cell's next unload, which is precisely
        // the bug #668 fixes, reintroduced one cell over.
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 20f, positionAccessor: PosAccessor);

        Entity e = host.SpawnOwned(50f, 50f, netId: 7, out CellSim source);
        source.World.Set(e, new Pos { X = 50f, Y = 50f });
        source.World.Set(e, new Blob { V = 1 });
        source.World.Set(e, new Transient { Scope = TransientScope.DurableOnly });

        source.World.Set(e, new Pos { X = 150f, Y = 50f });   // over the border into (1,0)
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim dest, out Entity moved));
        Assert.Equal(new CellCoord(1, 0), dest.Coord);
        Assert.True(dest.World.TryGet(moved, out Transient mark));
        Assert.Equal(TransientScope.DurableOnly, mark.Scope);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dest.SnapshotOwned(new HashSet<long>()));                       // never saved
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, dest.SnapshotOwned(new HashSet<long>(), SnapshotPurpose.Eviction));
    }

    [Fact]
    public void ADeferredMigrateCarriesTheScopeToo()
    {
        // The same carry over the link shape every cross-node implementation has: the scope is read in phase 1 of
        // one ProcessHandoffs call and re-applied in phase 2 of a later one, so it rides in the scratch map rather
        // than in the Migrate payload (which could not name an unregistered marker anyway).
        var link = new DeferringMigrateLink();
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 20f, positionAccessor: PosAccessor, cellLink: link);

        Entity e = host.SpawnOwned(50f, 50f, netId: 7, out CellSim source);
        source.World.Set(e, new Pos { X = 50f, Y = 50f });
        source.World.Set(e, new Blob { V = 1 });
        source.World.Set(e, new Transient { Scope = TransientScope.DurableOnly });

        source.World.Set(e, new Pos { X = 150f, Y = 50f });
        host.ProcessHandoffs();                               // sends the Migrate, which the link holds back
        link.DeliverStaged();
        host.ProcessHandoffs();                               // the destination adopts it on THIS call

        Assert.True(host.TryGetOwner(7, out CellSim dest, out Entity moved));
        Assert.True(dest.World.TryGet(moved, out Transient mark));
        Assert.Equal(TransientScope.DurableOnly, mark.Scope);
    }

    [Fact]
    public void AHandoffLeavesAnUnmarkedEntityPersistable()
    {
        // The other side of the same gate: the carry is keyed to the crossing entity, not applied to everything.
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 20f, positionAccessor: PosAccessor);

        Entity e = host.SpawnOwned(50f, 50f, netId: 7, out CellSim source);
        source.World.Set(e, new Pos { X = 50f, Y = 50f });
        source.World.Set(e, new Blob { V = 1 });

        source.World.Set(e, new Pos { X = 150f, Y = 50f });
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim dest, out Entity moved));
        Assert.False(dest.World.Has<Transient>(moved));
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, dest.SnapshotOwned(new HashSet<long>()));
    }
}
