// KhaozEngine server-stack NativeAOT gate probe.
// Stands up a one-cell ShardHost with a POPULATED ReplicationRegistry, spawns a few NetId entities
// carrying replicated components, runs a handful of server ticks (an ISystem integrates position
// each tick), then drives the real per-tick replication path end to end:
//   ServerReplicator.Capture(world) -> WriteFor(slot) -> ClientReplicationView.ApplyDelta(clientWorld)
// and checks the applied client state matches the server. This exercises Sharding (ShardHost / CellSim
// tick), Replication (registry closures, shared per-tick capture, delta encode/apply), and the Ecs
// generic component + query path - all deliberately reflection-free after the batch's items 2-4.
//
// The gate is that this LINKS, RUNS, and prints the expected sentinel under
//   dotnet publish -c Release -r <rid> (PublishAot=true)
// on a native arm64 mac. It prints one line starting "AOT PROBE:" with the checked values and returns
// a non-zero exit code on any mismatch (so the shell gate can assert exit 0).
using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

const float tick = 1f / 60f;
const int count = 3;

// Registry: two built-in ids (unframed wire) + one consumer extension id (length-prefixed, skippable).
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

// Verify: every server entity round-tripped to the client with matching position + health.
bool ok = client.Entities.Count == count;
long lastNetId = -1;
float lastClientX = 0f;
for (int i = 0; i < count && ok; i++)
{
    (long netId, Entity serverEntity) = serverEntities[i];
    ProbePosition sp = serverCell.World.Get<ProbePosition>(serverEntity);
    if (!client.TryGetEntity(netId, out Entity ce)) { ok = false; break; }
    ProbePosition cp = clientWorld.Get<ProbePosition>(ce);
    ProbeHealth ch = clientWorld.Get<ProbeHealth>(ce);
    if (MathF.Abs(sp.X - cp.X) > 1e-4f || MathF.Abs(sp.Y - cp.Y) > 1e-4f) ok = false;
    if (ch.Value != 100 - i) ok = false;
    lastNetId = netId;
    lastClientX = cp.X;
}

Console.WriteLine(
    $"AOT PROBE: entities={count} clientEntities={client.Entities.Count} lastNetId={lastNetId} clientX={lastClientX:F4} match={ok}");
return ok ? 0 : 1;

// ---- probe component + system types ----

/// <summary>Replicated 2D position (built-in id 1, interpolatable). Integrated each tick.</summary>
struct ProbePosition : IComponent { public float X; public float Y; }

/// <summary>Replicated 2D velocity (built-in id 2). Constant here, so it never re-sends after the baseline.</summary>
struct ProbeVelocity : IComponent { public float X; public float Y; }

/// <summary>Replicated health (consumer extension id 16, length-prefixed / skippable on the wire).</summary>
struct ProbeHealth : IComponent { public int Value; }

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
