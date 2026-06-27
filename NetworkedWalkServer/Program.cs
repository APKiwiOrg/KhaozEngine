using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;
using KhaozEngine.WorldStore.Sqlite;

// Headless authoritative server for the networked walkable slice, now SHARDED: the shipped analytic terrain
// (TerrainPresets.Clearing) is the ground, and a multi-cell ShardedWorldServer runs the movement stack across a
// grid of authoritative cells (cellSize 60 = one terrain chunk) over a LiteNetLib UDP socket. Players are owned by
// the cell containing them; walking across a cell boundary hands authority off seamlessly (NetId stable, no hitch),
// and two players in adjacent cells see each other via border ghosting. Players persist to an embedded SQLite DB via
// WorldPersistence (keyed player:{accountId}, cell-agnostic), so disconnect/reconnect (or a process restart) restores
// position - in whatever cell now contains it. Connect two NetworkedWalkSample clients to see it.
// Usage: NetworkedWalkServer [port] [dbPath].
int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47700;
string dbPath = args.Length > 1 ? args[1] : "networked-walk-world.db";

var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new ShardedWorldServerConfig
{
    TickSeconds = 1f / 30f,
    CellSize = 60f,              // one terrain chunk (TerrainChunkRegion.DefaultSize) per cell
    OverlapMargin = 24f,        // border ghost band; >= InterestRadius
    InterestRadius = 24f,
    MaxPlayers = 16,
    // Spread joiners across the central cells on the walkable meadow (z<48): slot 0 in cell (0,0), slot 1 in (1,0),
    // both near the shared x=60 border and within view of each other. Walk one east to cross the border.
    SpawnPosition = slot => new Vector3(48f + slot * 20f, 0f, 24f),
};

using var transport = new LiteNetLibServerTransport(port);
var server = new ShardedWorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

// Persist players keyed by the account token the client presents in its Hello. Swap SqliteWorldStore for
// SqlServerWorldStore (KhaozEngine.WorldStore.SqlServer) to persist to Azure SQL instead - same IWorldStore.
using var store = new SqliteWorldStore($"Data Source={dbPath}");
var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 10f });

var clock = new FixedTickHost(config.TickSeconds);
var sw = Stopwatch.StartNew();
double last = 0;
Console.WriteLine($"Sharded walk server on UDP {port} (tick {1f / config.TickSeconds:0} Hz, cellSize {config.CellSize}), persisting to {dbPath}. Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    persistence.FlushAsync().GetAwaiter().GetResult();   // save everyone before exit
    Console.WriteLine("Saved world. Bye.");
    Environment.Exit(0);
};

while (true)
{
    server.Poll();
    double now = sw.Elapsed.TotalSeconds;
    float elapsed = (float)(now - last);
    last = now;
    clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));
    persistence.Update(elapsed);
    Thread.Sleep(5);
}
