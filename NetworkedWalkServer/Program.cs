using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;

// Headless authoritative server for the networked walkable slice: the shipped analytic terrain
// (TerrainPresets.Clearing) is the ground, a single-World WorldServer runs PlayerMoveSimulator on a
// FixedTickHost over a LiteNetLib UDP socket, and one player entity spawns per connection. Connect two
// NetworkedWalkSample clients to see them walk the same terrain. Usage: NetworkedWalkServer [port].
int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47700;

var field = new TerrainField(TerrainPresets.Clearing());
var terrain = new TerrainCollision(field);
var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 200f, MaxPlayers = 16 };

using var transport = new LiteNetLibServerTransport(port);
var server = new WorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

var clock = new FixedTickHost(config.TickSeconds);
var sw = Stopwatch.StartNew();
double last = 0;
Console.WriteLine($"Networked walk server on UDP {port} (tick {1f / config.TickSeconds:0} Hz). Ctrl+C to stop.");
while (true)
{
    server.Poll();
    double now = sw.Elapsed.TotalSeconds;
    float elapsed = (float)(now - last);
    last = now;
    clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));
    Thread.Sleep(5);
}
