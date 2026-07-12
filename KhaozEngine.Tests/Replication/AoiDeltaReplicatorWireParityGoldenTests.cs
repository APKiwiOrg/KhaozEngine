using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Byte-exact wire golden for <see cref="AoiDeltaReplicator"/>. A single fully-scripted multi-tick scenario (two
/// clients, a plain replicated component and an owner-scoped one, entities entering and leaving AoI, an ack skip,
/// and a despawn) is encoded once and every per-client per-tick frame is asserted against a recorded golden. The
/// goldens were captured from the pre-share per-client implementation, so this test pins the exact wire the shared
/// per-tick capture must reproduce byte-for-byte: it is committed green against the old code and stays green through
/// the refactor.
/// </summary>
public class AoiDeltaReplicatorWireParityGoldenTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private struct Secret : IComponent { public int V; }   // owner-only extension component

    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId; // 16

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Secret>(
            typeId: SecretId,
            write: (s, bw) => bw.Write(s.V),
            read: br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        return r;
    }

    private static Entity Spawn(World w, IDictionary<long, Entity> handles, long netId, float x, float y, int? secret)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Pos { X = x, Y = y });
        if (secret is int v) w.Set(e, new Secret { V = v });
        handles[netId] = e;
        return e;
    }

    private static HashSet<long> Aoi(params long[] ids) => new(ids);

    // The fixed scenario, driven exactly as production drives it (one BeginTick per tick, one WriteFor per client per
    // tick, acks between ticks). Returns every frame in a stable order: per tick, slot 0 then slot 1.
    private static List<byte[]> RunScenario()
    {
        ReplicationRegistry registry = NewRegistry();
        var world = new World();
        var h = new Dictionary<long, Entity>();
        var frames = new List<byte[]>();

        const int slotA = 0, slotB = 1;         // A owns netId 1, B owns netId 2
        const long ownerA = 1, ownerB = 2;

        var repl = new AoiDeltaReplicator(registry);

        // Players (1, 2) carry an owner-only Secret. NPCs (3, 4) do not.
        Spawn(world, h, 1, 0f, 0f, secret: 111);
        Spawn(world, h, 2, 10f, 0f, secret: 222);
        Spawn(world, h, 3, 5f, 5f, secret: null);

        // Tick 1: full snapshots. A sees {1,2,3}, B sees {1,2,3}. Both ack.
        int seq1 = repl.BeginTick();
        frames.Add(repl.WriteFor(slotA, world, Aoi(1, 2, 3), ownerA));
        frames.Add(repl.WriteFor(slotB, world, Aoi(1, 2, 3), ownerB));
        repl.Acknowledge(slotA, seq1);
        repl.Acknowledge(slotB, seq1);

        // Tick 2: entity 1 moves, entity 4 spawns. A sees {1,2,3,4} (4 enters). B sees {2,3,4} (1 leaves B's AoI,
        // 4 enters). B acks seq2. A does NOT ack (ack skip), so A keeps diffing from seq1.
        world.Set(h[1], new Pos { X = 1f, Y = 2f });
        Spawn(world, h, 4, 20f, 0f, secret: null);
        int seq2 = repl.BeginTick();
        frames.Add(repl.WriteFor(slotA, world, Aoi(1, 2, 3, 4), ownerA));
        frames.Add(repl.WriteFor(slotB, world, Aoi(2, 3, 4), ownerB));
        repl.Acknowledge(slotB, seq2);

        // Tick 3: entity 1 moves again, entity 3 despawns. A sees {1,2,4} (3 gone), diffing from seq1 (its seq2 ack
        // was lost). B sees {2,4}, diffing from seq2.
        world.Set(h[1], new Pos { X = 3f, Y = 4f });
        world.Despawn(h[3]);
        repl.BeginTick();
        frames.Add(repl.WriteFor(slotA, world, Aoi(1, 2, 4), ownerA));
        frames.Add(repl.WriteFor(slotB, world, Aoi(2, 4), ownerB));

        return frames;
    }

    // Golden frames, base64, in RunScenario's emission order (tick1 A, tick1 B, tick2 A, tick2 B, tick3 A, tick3 B).
    private static readonly string[] Golden =
    {
        // tick 1, slot A (owner 1): full {1,2,3}, Secret only on entity 1
        "/////wEAAAAAAAAAAwAAAAMAAAAAAAAAAQAAAAABAAAAoEAAAKBAAAABAAAAAAAAAAEAAAAAAQAAAAAAAAAAABAABG8AAAAAAAIAAAAAAAAAAQAAAAABAAAAIEEAAAAAAAA=",
        // tick 1, slot B (owner 2): full {1,2,3}, Secret only on entity 2
        "/////wEAAAAAAAAAAwAAAAMAAAAAAAAAAQAAAAABAAAAoEAAAKBAAAABAAAAAAAAAAEAAAAAAQAAAAAAAAAAAAAAAgAAAAAAAAABAAAAAAEAAAAgQQAAAAAQAATeAAAAAAA=",
        // tick 2, slot A (owner 1): entity 1 moved, entity 4 enters. diff from seq1
        "AQAAAAIAAAAAAAAAAgAAAAQAAAAAAAAAAQAAAAABAAAAoEEAAAAAAAABAAAAAAAAAAAAAAAAAQAAAIA/AAAAQAAA",
        // tick 2, slot B (owner 2): entity 1 leaves AoI (removed), entity 4 enters. diff from seq1
        "AQAAAAIAAAABAAAAAQAAAAAAAAABAAAABAAAAAAAAAABAAAAAAEAAACgQQAAAAAAAA==",
        // tick 3, slot A (owner 1): diff from seq1 (seq2 ack lost). entity 3 removed, 1 and 4 carried
        "AQAAAAMAAAABAAAAAwAAAAAAAAACAAAABAAAAAAAAAABAAAAAAEAAACgQQAAAAAAAAEAAAAAAAAAAAAAAAABAAAAQEAAAIBAAAA=",
        // tick 3, slot B (owner 2): diff from seq2. entity 3 removed, nothing changed
        "AgAAAAMAAAABAAAAAwAAAAAAAAAAAAAA",
    };

    [Fact]
    public void Scenario_ProducesByteIdenticalWirePerClientPerTick()
    {
        List<byte[]> frames = RunScenario();

        // DUMP: to regenerate, base64-encode each frame (frames.ConvertAll(Convert.ToBase64String)) and re-bake the
        // Golden constants above. The scenario is fully deterministic, so the goldens are stable.

        Assert.Equal(Golden.Length, frames.Count);
        for (int i = 0; i < frames.Count; i++)
            Assert.Equal(Golden[i], Convert.ToBase64String(frames[i]));
    }

    // A decode cross-check: the goldens are not just stable bytes, they decode to the states each client should hold.
    // This guards against a golden that is byte-stable but semantically wrong (so a later refactor matching the bytes
    // is genuinely matching correct output).
    [Fact]
    public void Scenario_GoldenFramesDecodeToTheExpectedClientState()
    {
        List<byte[]> frames = RunScenario();
        ReplicationRegistry registry = NewRegistry();

        var worldA = new World();
        var viewA = new ClientReplicationView(registry);
        var worldB = new World();
        var viewB = new ClientReplicationView(registry);

        // Apply every A frame, then every B frame, in tick order.
        viewA.ApplyDelta(worldA, frames[0]);
        viewB.ApplyDelta(worldB, frames[1]);
        viewA.ApplyDelta(worldA, frames[2]);
        viewB.ApplyDelta(worldB, frames[3]);
        viewA.ApplyDelta(worldA, frames[4]);
        viewB.ApplyDelta(worldB, frames[5]);

        // A: entity 1 moved to (3,4), 2 present, 3 despawned, 4 present. A owns 1, so only 1 carries Secret.
        Assert.True(viewA.TryGetEntity(1, out Entity a1));
        Assert.Equal(3f, worldA.Get<Pos>(a1).X);
        Assert.True(worldA.Has<Secret>(a1));          // owner sees its own owner-only component
        Assert.True(viewA.TryGetEntity(2, out Entity a2));
        Assert.False(worldA.Has<Secret>(a2));         // observer never sees another player's owner-only component
        Assert.False(viewA.TryGetEntity(3, out _));   // despawned
        Assert.True(viewA.TryGetEntity(4, out _));

        // B: entity 1 left B's AoI (removed), 2 present with its own Secret, 3 despawned, 4 present.
        Assert.False(viewB.TryGetEntity(1, out _));
        Assert.True(viewB.TryGetEntity(2, out Entity b2));
        Assert.True(worldB.Has<Secret>(b2));
        Assert.False(viewB.TryGetEntity(3, out _));
        Assert.True(viewB.TryGetEntity(4, out _));
    }

    // ---- Plain-registry golden: no component in this registry carries ReplicationChannels.OwnerOnly, so
    // AoiDeltaReplicator's hasOwnerScopedCodec is false and Project takes its fast path, referencing each in-AoI
    // entity's captured component dictionary directly from the shared capture instead of building a filtered
    // per-client copy. The owner-scoped golden above never exercises that branch, so this pins it separately with
    // the same scripted-scenario shape (two clients, entities entering and leaving AoI, an ack skip, a despawn).

    private static ReplicationRegistry NewPlainRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static Entity SpawnPlain(World w, IDictionary<long, Entity> handles, long netId, float x, float y)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Pos { X = x, Y = y });
        handles[netId] = e;
        return e;
    }

    // Same shape as RunScenario, minus the owner-scoped Secret component: two clients, an entity moving in and out
    // of AoI, an ack skip on one slot, and a despawn. Returns frames in tick1 A, tick1 B, tick2 A, tick2 B, tick3 A,
    // tick3 B order.
    private static List<byte[]> RunPlainScenario()
    {
        ReplicationRegistry registry = NewPlainRegistry();
        var world = new World();
        var h = new Dictionary<long, Entity>();
        var frames = new List<byte[]>();

        const int slotA = 0, slotB = 1;         // A owns netId 1, B owns netId 2
        const long ownerA = 1, ownerB = 2;

        var repl = new AoiDeltaReplicator(registry);

        SpawnPlain(world, h, 1, 0f, 0f);
        SpawnPlain(world, h, 2, 10f, 0f);
        SpawnPlain(world, h, 3, 5f, 5f);

        // Tick 1: full snapshots. A sees {1,2,3}, B sees {1,2,3}. Both ack.
        int seq1 = repl.BeginTick();
        frames.Add(repl.WriteFor(slotA, world, Aoi(1, 2, 3), ownerA));
        frames.Add(repl.WriteFor(slotB, world, Aoi(1, 2, 3), ownerB));
        repl.Acknowledge(slotA, seq1);
        repl.Acknowledge(slotB, seq1);

        // Tick 2: entity 1 moves, entity 4 spawns. A sees {1,2,3,4} (4 enters). B sees {2,3,4} (1 leaves B's AoI,
        // 4 enters). B acks seq2. A does NOT ack (ack skip), so A keeps diffing from seq1.
        world.Set(h[1], new Pos { X = 1f, Y = 2f });
        SpawnPlain(world, h, 4, 20f, 0f);
        int seq2 = repl.BeginTick();
        frames.Add(repl.WriteFor(slotA, world, Aoi(1, 2, 3, 4), ownerA));
        frames.Add(repl.WriteFor(slotB, world, Aoi(2, 3, 4), ownerB));
        repl.Acknowledge(slotB, seq2);

        // Tick 3: entity 1 moves again, entity 3 despawns. A sees {1,2,4} (3 gone), diffing from seq1 (its seq2 ack
        // was lost). B sees {2,4}, diffing from seq2.
        world.Set(h[1], new Pos { X = 3f, Y = 4f });
        world.Despawn(h[3]);
        repl.BeginTick();
        frames.Add(repl.WriteFor(slotA, world, Aoi(1, 2, 4), ownerA));
        frames.Add(repl.WriteFor(slotB, world, Aoi(2, 4), ownerB));

        return frames;
    }

    // Golden frames, base64, in RunPlainScenario's emission order (tick1 A, tick1 B, tick2 A, tick2 B, tick3 A, tick3 B).
    private static readonly string[] PlainRegistryGolden =
    {
        // tick 1, slot A: full {1,2,3}
        "/////wEAAAAAAAAAAwAAAAEAAAAAAAAAAQAAAAABAAAAAAAAAAAAAAACAAAAAAAAAAEAAAAAAQAAACBBAAAAAAAAAwAAAAAAAAABAAAAAAEAAACgQAAAoEAAAA==",
        // tick 1, slot B: full {1,2,3}
        "/////wEAAAAAAAAAAwAAAAEAAAAAAAAAAQAAAAABAAAAAAAAAAAAAAACAAAAAAAAAAEAAAAAAQAAACBBAAAAAAAAAwAAAAAAAAABAAAAAAEAAACgQAAAoEAAAA==",
        // tick 2, slot A: entity 1 moved, entity 4 enters. diff from seq1
        "AQAAAAIAAAAAAAAAAgAAAAEAAAAAAAAAAAAAAAABAAAAgD8AAABAAAAEAAAAAAAAAAEAAAAAAQAAAKBBAAAAAAAA",
        // tick 2, slot B: entity 1 leaves AoI (removed), entity 4 enters. diff from seq1
        "AQAAAAIAAAABAAAAAQAAAAAAAAABAAAABAAAAAAAAAABAAAAAAEAAACgQQAAAAAAAA==",
        // tick 3, slot A: diff from seq1 (seq2 ack lost). entity 3 removed, 1 and 4 carried
        "AQAAAAMAAAABAAAAAwAAAAAAAAACAAAAAQAAAAAAAAAAAAAAAAEAAABAQAAAgEAAAAQAAAAAAAAAAQAAAAABAAAAoEEAAAAAAAA=",
        // tick 3, slot B: diff from seq2. entity 3 removed, nothing changed
        "AgAAAAMAAAABAAAAAwAAAAAAAAAAAAAA",
    };

    [Fact]
    public void PlainRegistryScenario_ProducesByteIdenticalWirePerClientPerTick()
    {
        List<byte[]> frames = RunPlainScenario();

        // DUMP: to regenerate, base64-encode each frame (frames.ConvertAll(Convert.ToBase64String)) and re-bake the
        // PlainRegistryGolden constants above. The scenario is fully deterministic, so the goldens are stable.

        Assert.Equal(PlainRegistryGolden.Length, frames.Count);
        for (int i = 0; i < frames.Count; i++)
            Assert.Equal(PlainRegistryGolden[i], Convert.ToBase64String(frames[i]));
    }

    // A decode cross-check: the goldens are not just stable bytes, they decode to the states each client should
    // hold. This guards against a golden that is byte-stable but semantically wrong.
    [Fact]
    public void PlainRegistryScenario_GoldenFramesDecodeToTheExpectedClientState()
    {
        List<byte[]> frames = RunPlainScenario();
        ReplicationRegistry registry = NewPlainRegistry();

        var worldA = new World();
        var viewA = new ClientReplicationView(registry);
        var worldB = new World();
        var viewB = new ClientReplicationView(registry);

        // Apply every A frame, then every B frame, in tick order.
        viewA.ApplyDelta(worldA, frames[0]);
        viewB.ApplyDelta(worldB, frames[1]);
        viewA.ApplyDelta(worldA, frames[2]);
        viewB.ApplyDelta(worldB, frames[3]);
        viewA.ApplyDelta(worldA, frames[4]);
        viewB.ApplyDelta(worldB, frames[5]);

        // A: entity 1 moved to (3,4), 2 present, 3 despawned, 4 present.
        Assert.True(viewA.TryGetEntity(1, out Entity a1));
        Assert.Equal(3f, worldA.Get<Pos>(a1).X);
        Assert.True(viewA.TryGetEntity(2, out _));
        Assert.False(viewA.TryGetEntity(3, out _));   // despawned
        Assert.True(viewA.TryGetEntity(4, out _));

        // B: entity 1 left B's AoI (removed), 2 present, 3 despawned, 4 present.
        Assert.False(viewB.TryGetEntity(1, out _));
        Assert.True(viewB.TryGetEntity(2, out _));
        Assert.False(viewB.TryGetEntity(3, out _));
        Assert.True(viewB.TryGetEntity(4, out _));
    }
}
