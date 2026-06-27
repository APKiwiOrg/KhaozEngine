using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;
using KhaozEngine.WorldStore.Sqlite;

// Headless authoritative server for the networked walkable slice: the shipped analytic terrain
// (TerrainPresets.Clearing) is the ground, a single-World WorldServer runs PlayerMoveSimulator on a
// FixedTickHost over a LiteNetLib UDP socket, and one player entity spawns per connection. Players persist to
// an embedded SQLite DB via WorldPersistence, so walking somewhere, disconnecting, and reconnecting (or
// restarting this process) restores position. Connect two NetworkedWalkSample clients to see them walk the
// same terrain. Usage: NetworkedWalkServer [port] [dbPath].
int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47700;
string dbPath = args.Length > 1 ? args[1] : "networked-walk-world.db";

var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 200f, MaxPlayers = 16 };

using var transport = new LiteNetLibServerTransport(port);
var server = new WorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

// Persist players keyed by the account token the client presents in its Hello. Swap SqliteWorldStore for
// SqlServerWorldStore (KhaozEngine.WorldStore.SqlServer) to persist to Azure SQL instead - same IWorldStore.
using var store = new SqliteWorldStore($"Data Source={dbPath}");
var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 10f });

var clock = new FixedTickHost(config.TickSeconds);
var sw = Stopwatch.StartNew();
double last = 0;
Console.WriteLine($"Networked walk server on UDP {port} (tick {1f / config.TickSeconds:0} Hz), persisting to {dbPath}. Ctrl+C to stop.");

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
