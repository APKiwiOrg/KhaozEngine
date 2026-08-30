using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// The buffer-filling <c>HomeInterest</c> overload (#134). The sharded serve loop used to take a freshly allocated
/// <see cref="HashSet{T}"/> per client per tick out of the allocating overload; it now fills one set it reuses
/// across the tick's clients. These pin that the two overloads agree, that the new one adds rather than clears (so
/// the caller owns the reset between clients), and that a reused set carries nothing between clients once cleared.
/// </summary>
public class ShardHostInterestBufferTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    private static ShardHost ServingHost() =>
        new(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
            overlapMargin: 30f, positionAccessor: PosAccessor);

    private static void SpawnOwned(ShardHost host, int netId, float x, float y)
    {
        Entity e = host.SpawnAt(x, y, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
    }

    // Two clients in the same cell, far enough apart that a radius of 10 sees only the caller's own player.
    private static ShardHost TwoDistantClients()
    {
        ShardHost host = ServingHost();
        SpawnOwned(host, 1, 10f, 50f);
        SpawnOwned(host, 2, 90f, 50f);
        host.BindClient(0, 1);
        host.BindClient(1, 2);
        return host;
    }

    [Fact]
    public void TheBufferOverloadMatchesTheAllocatingOne()
    {
        ShardHost host = TwoDistantClients();
        (World expectedWorld, HashSet<long> expected) = host.HomeInterest(0, interestRadius: 30f);

        var actual = new HashSet<long>();
        World actualWorld = host.HomeInterest(0, 30f, actual);

        Assert.Same(expectedWorld, actualWorld);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ItAddsRatherThanClears()
    {
        // Same contract as InterestGrid.Query(float, float, float, ICollection<long>): the caller owns the reset,
        // which is what lets a serve loop accumulate across cells if it ever wants to.
        ShardHost host = TwoDistantClients();
        var results = new HashSet<long> { 9999L };

        host.HomeInterest(0, 30f, results);

        Assert.Contains(9999L, results);
        Assert.Contains(1L, results);
    }

    [Fact]
    public void AReusedSetCarriesNothingBetweenClients()
    {
        // The serve loop's actual pattern: one set, cleared per client. Client 1's AoI must not inherit client 0's.
        ShardHost host = TwoDistantClients();
        var scratch = new HashSet<long>();

        scratch.Clear();
        host.HomeInterest(0, 10f, scratch, serveEpoch: 1);
        Assert.Equal(new HashSet<long> { 1L }, scratch);

        scratch.Clear();
        host.HomeInterest(1, 10f, scratch, serveEpoch: 1);
        Assert.Equal(new HashSet<long> { 2L }, scratch);
    }

    [Fact]
    public void ItHonoursTheSharedServeEpoch()
    {
        ShardHost host = TwoDistantClients();
        Assert.True(host.TryGetHomeCell(0, out CellSim home));
        var scratch = new HashSet<long>();

        host.HomeInterest(0, 30f, scratch, serveEpoch: 1);
        scratch.Clear();
        host.HomeInterest(1, 30f, scratch, serveEpoch: 1);
        Assert.Equal(1, home.InterestRebuildCount);

        scratch.Clear();
        host.HomeInterest(0, 30f, scratch, serveEpoch: 2);
        Assert.Equal(2, home.InterestRebuildCount);
    }

    [Fact]
    public void ANullBufferIsRejected()
    {
        // The cast is load-bearing and worth pinning: a bare `null` third argument still binds to the older
        // HomeInterest(int, float, long?) overload (no omitted optional beats one), so adding this overload did not
        // change what an existing `HomeInterest(slot, radius, null)` call means.
        ShardHost host = TwoDistantClients();
        Assert.Throws<ArgumentNullException>(() => host.HomeInterest(0, 30f, (ICollection<long>)null!));
    }

    [Fact]
    public void TheSameValidationStillApplies()
    {
        ShardHost host = TwoDistantClients();
        var scratch = new HashSet<long>();

        Assert.Throws<ArgumentOutOfRangeException>(() => host.HomeInterest(0, -1f, scratch));
        Assert.Throws<InvalidOperationException>(() => host.HomeInterest(7, 30f, scratch));
    }
}
