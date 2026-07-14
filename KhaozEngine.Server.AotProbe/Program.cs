// KhaozEngine server-stack NativeAOT gate probe.
// Stands up a one-cell ShardHost with a POPULATED ReplicationRegistry, spawns a few NetId entities
// carrying replicated components, runs a handful of server ticks (an ISystem integrates position
// each tick), then drives the real per-tick replication path end to end:
//   ServerReplicator.Capture(world) -> WriteFor(slot) -> ClientReplicationView.ApplyDelta(clientWorld)
// and checks the applied client state matches the server. This exercises Sharding (ShardHost / CellSim
// tick), Replication (registry closures, shared per-tick capture, delta encode/apply), and the Ecs
// generic component + query path - all deliberately reflection-free after the batch's items 2-4.
//
// The component set covers every branch of the reflection-free ECS tag classification
// (ComponentRegistry.TagInfo<T>, the replacement for the AOT-unsafe GetFields): multi-byte structs
// (position/velocity/health, the size > 1 shortcut), a zero-field tag (size 1, byte-flip compares
// equal), and a single-byte-field flag (size 1, byte-flip compares unequal). The two size-1 cases are
// the ambiguous ones only the flip + EqualityComparer disambiguation gets right under AOT, so this
// gate proves the replacement mechanism itself, not just the paths that avoided the old bug.
//
// It also gates the two subsystems that went NativeAOT-clean alongside the tick path: the ECS world JSON
// save/load path (WorldSerializer round-trip through the generic column-factory seam + source-generated
// envelope) and the durable NetWorld persistence DTOs (PlayerRecord / WorldMetaRecord /
// WorldStoreBanStore, each through a source-generated JsonSerializerContext).
//
// The gate is that this LINKS, RUNS, and prints the expected sentinel under
//   dotnet publish -c Release -r <rid> (PublishAot=true)
// on a native arm64 mac. It prints one line starting "AOT PROBE:" with the checked values and returns
// a non-zero exit code on any mismatch (so the shell gate can assert exit 0).
using System;
using System.Numerics;
using System.Text.Json.Serialization;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;

const float tick = 1f / 60f;
const int count = 3;

// Registry: four built-in ids (unframed wire) + one consumer extension id (length-prefixed, skippable).
// Register<T> closes over T statically - no reflection, no Activator - so it is AOT-clean by construction.
var registry = new ReplicationRegistry();
registry.Register<ProbePosition>(1,
    (p, w) => { w.Write(p.X); w.Write(p.Y); },
    r => new ProbePosition { X = r.ReadSingle(), Y = r.ReadSingle() },
    lerp: (a, b, t) => new ProbePosition { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
registry.Register<ProbeVelocity>(2,
    (v, w) => { w.Write(v.X); w.Write(v.Y); },
    r => new ProbeVelocity { X = r.ReadSingle(), Y = r.ReadSingle() });
registry.Register<ProbeHealth>(ReplicationRegistry.FirstExtensionTypeId,   // id 16: length-prefixed extension
    (h, w) => w.Write(h.Value),
    r => new ProbeHealth { Value = r.ReadInt32() });
registry.Register<ProbeElite>(3,   // zero-field tag: zero payload bytes, presence itself is the state
    (t, w) => { },
    r => default);
registry.Register<ProbeFlag>(4,    // single byte field: size 1 like the tag, but stored, not a tag
    (f, w) => w.Write(f.Value),
    r => new ProbeFlag { Value = r.ReadByte() });

// Server: a one-cell shard host, a few owned entities (NetId 1..count) all in the same cell, plus a
// per-tick integrate system so ticks actually mutate replicated state.
var host = new ShardHost(cellSize: 64f, tickSeconds: tick, registry);
var serverEntities = new (long netId, Entity entity)[count];
CellSim serverCell = null!;
for (int i = 0; i < count; i++)
{
    long netId = i + 1;
    Entity e = host.SpawnOwned(10f + i, 10f + i, netId, out CellSim cell);
    cell.World.Set(e, new ProbePosition { X = 10f + i, Y = 10f + i });
    cell.World.Set(e, new ProbeVelocity { X = 1f + i, Y = -1f - i });
    cell.World.Set(e, new ProbeHealth { Value = 100 - i });
    cell.World.Set(e, new ProbeFlag { Value = (byte)(i + 1) });
    // The tag goes on even-indexed entities only, so both PRESENCE and ABSENCE must round-trip (a
    // tag-misclassified stored component, or vice versa, would break one of the two).
    if (i % 2 == 0) cell.World.Set(e, new ProbeElite());
    serverEntities[i] = (netId, e);
    serverCell = cell;   // one cell here, so every entity lands in the same one
}
foreach (CellSim cell in host.Cells)
    cell.World.AddSystem(new ProbeIntegrateSystem());

var server = new ServerReplicator(registry);

// Client: a fresh world + view over the same registry (server and client must share ids/codecs).
var clientWorld = new World();
var client = new ClientReplicationView(registry);

// A few warm ticks, then the first delta (baseline -1 = a full snapshot) applied to the client.
for (int i = 0; i < 3; i++) host.Tick(tick, maxTicksPerFrame: 1);
int seq = server.Capture(serverCell.World);
byte[] full = server.WriteFor(slot: 0);
client.ApplyDelta(clientWorld, full);
server.Acknowledge(slot: 0, seq);

// More ticks move the entities, then an incremental delta carries only the changed positions.
for (int i = 0; i < 5; i++) host.Tick(tick, maxTicksPerFrame: 1);
seq = server.Capture(serverCell.World);
byte[] delta = server.WriteFor(slot: 0);
client.ApplyDelta(clientWorld, delta);
server.Acknowledge(slot: 0, seq);

// Verify: every server entity round-tripped to the client with matching position, health, flag
// (the size-1 stored component's VALUE survives), and tag presence/absence (the zero-field tag's
// PRESENCE survives, and it never leaks onto entities that don't carry it).
bool ok = client.Entities.Count == count;
long lastNetId = -1;
float lastClientX = 0f;
int clientTags = 0;
for (int i = 0; i < count && ok; i++)
{
    (long netId, Entity serverEntity) = serverEntities[i];
    ProbePosition sp = serverCell.World.Get<ProbePosition>(serverEntity);
    if (!client.TryGetEntity(netId, out Entity ce)) { ok = false; break; }
    ProbePosition cp = clientWorld.Get<ProbePosition>(ce);
    ProbeHealth ch = clientWorld.Get<ProbeHealth>(ce);
    ProbeFlag cf = clientWorld.Get<ProbeFlag>(ce);
    if (MathF.Abs(sp.X - cp.X) > 1e-4f || MathF.Abs(sp.Y - cp.Y) > 1e-4f) ok = false;
    if (ch.Value != 100 - i) ok = false;
    if (cf.Value != (byte)(i + 1)) ok = false;
    bool hasTag = clientWorld.Has<ProbeElite>(ce);
    if (hasTag != (i % 2 == 0)) ok = false;   // presence AND absence both round-trip
    if (hasTag) clientTags++;
    lastNetId = netId;
    lastClientX = cp.X;
}

// ---- ECS world JSON save/load (WorldSerializer) ----
// Register the component set through the generic, reflection-free seam (Create().Add<T>()), and pass JSON options
// backed by a source-generated context for the component structs. Save + Load round-trip a small world, including a
// zero-field tag (ProbeElite), so the source-generated envelope + Type-keyed column factory both run under AOT.
var saveSer = WorldSerializer.Create()
    .Add<ProbePosition>()
    .Add<ProbeHealth>()
    .Add<ProbeFlag>()
    .Add<ProbeElite>()
    .Build(ProbeJsonContext.Default.Options);
var saveWorld = new World();
Entity saved0 = saveWorld.Spawn();
saveWorld.Set(saved0, new ProbePosition { X = 3f, Y = 4f });
saveWorld.Set(saved0, new ProbeHealth { Value = 42 });
saveWorld.Set(saved0, new ProbeElite());               // zero-field tag: presence must survive save/load under AOT
Entity saved1 = saveWorld.Spawn();
saveWorld.Set(saved1, new ProbeFlag { Value = 7 });
World reloaded = saveSer.Load(saveSer.Save(saveWorld));
bool saveOk =
    reloaded.IsAlive(saved0) && reloaded.IsAlive(saved1) &&
    MathF.Abs(reloaded.Get<ProbePosition>(saved0).X - 3f) < 1e-4f &&
    reloaded.Get<ProbeHealth>(saved0).Value == 42 &&
    reloaded.Has<ProbeElite>(saved0) &&
    reloaded.Get<ProbeFlag>(saved1).Value == 7;

// ---- NetWorld durable DTO round-trips (source-generated contexts) ----
var playerState = new PlayerMoveState { Position = new Vector3(1.5f, 2.5f, -3.5f) };
PlayerRecord decodedPlayer = PlayerRecord.Decode(PlayerRecord.From(playerState).Encode());
bool playerOk = decodedPlayer.ToState().Position == playerState.Position;

WorldMetaRecord decodedMeta = WorldMetaRecord.Decode(new WorldMetaRecord { NextNetId = 123456789L }.Encode());
bool metaOk = decodedMeta.NextNetId == 123456789L;

// The ban round-trip exercises WorldStoreBanStore.BanDto encode/decode over an in-memory store.
var banStore = new WorldStoreBanStore(new InMemoryWorldStore());
banStore.BanAsync("acct-1", "probe").AsTask().GetAwaiter().GetResult();
bool banOk = banStore.IsBanned("acct-1") && !banStore.IsBanned("acct-2");

ok = ok && saveOk && playerOk && metaOk && banOk;

Console.WriteLine(
    $"AOT PROBE: entities={count} clientEntities={client.Entities.Count} tags={clientTags} lastNetId={lastNetId} " +
    $"clientX={lastClientX:F4} save={saveOk} player={playerOk} meta={metaOk} ban={banOk} match={ok}");
return ok ? 0 : 1;

// ---- probe component + system types ----

/// <summary>Replicated 2D position (built-in id 1, interpolatable). Integrated each tick.</summary>
struct ProbePosition : IComponent { public float X; public float Y; }

/// <summary>Replicated 2D velocity (built-in id 2). Constant here, so it never re-sends after the baseline.</summary>
struct ProbeVelocity : IComponent { public float X; public float Y; }

/// <summary>Replicated health (consumer extension id 16, length-prefixed / skippable on the wire).</summary>
struct ProbeHealth : IComponent { public int Value; }

/// <summary>Zero-field replicated tag (built-in id 3, zero payload bytes). Size 1 with no fields: the
/// TagInfo byte-flip must classify it a TAG (no column), and its wire presence must round-trip.</summary>
struct ProbeElite : IComponent { }

/// <summary>Single-byte replicated flag (built-in id 4). Size 1 WITH a field: the TagInfo byte-flip
/// must classify it STORED (a real column), and its value must round-trip.</summary>
struct ProbeFlag : IComponent { public byte Value; }

/// <summary>Integrates <see cref="ProbePosition"/> by <see cref="ProbeVelocity"/> once per fixed tick.</summary>
sealed class ProbeIntegrateSystem : ISystem
{
    public void Update(World world, float dt) =>
        world.ForEach((Entity _, ref ProbePosition p, ref ProbeVelocity v) =>
        {
            p.X += v.X * dt;
            p.Y += v.Y * dt;
        });
}

/// <summary>Source-generated JSON context for the probe's component structs, so the WorldSerializer save/load path
/// resolves their per-type JSON without reflection under NativeAOT. IncludeFields because the components use fields.</summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(ProbePosition))]
[JsonSerializable(typeof(ProbeHealth))]
[JsonSerializable(typeof(ProbeFlag))]
[JsonSerializable(typeof(ProbeElite))]
internal partial class ProbeJsonContext : JsonSerializerContext { }
