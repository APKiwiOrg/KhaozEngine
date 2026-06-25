using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.Simulation;

namespace MmoServerSample;

/// <summary>
/// Boots the reference dedicated MMO server on a real UDP socket (LiteNetLib) and runs the authoritative loop on a
/// <see cref="FixedTickHost"/>. A thin client can connect, send move commands, and walk across cell boundaries
/// (authority hands off seamlessly; the home cell serves its whole area-of-interest). The headless equivalent over
/// <c>LoopbackTransport</c> is exercised in the test suite.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47600;
        var config = new MmoServerConfig();

        using var transport = new LiteNetLibServerTransport(port);
        var server = new MmoServer(transport, config);

        // Seed a small static world so a connecting client has neighbours to see across cell borders.
        server.SpawnNpc(config.CellSize * 0.5f, config.CellSize * 0.5f);
        server.SpawnNpc(config.CellSize * 1.5f, config.CellSize * 0.5f);
        server.SpawnNpc(config.CellSize * 1.0f, config.CellSize * 1.0f);

        var clock = new FixedTickHost(config.TickSeconds);
        var sw = Stopwatch.StartNew();
        double last = 0;

        Console.WriteLine($"MMO server listening on UDP {port} (tick {1f / config.TickSeconds:0} Hz). Ctrl+C to stop.");
        while (true)
        {
            server.Poll();
            double now = sw.Elapsed.TotalSeconds;
            float elapsed = (float)(now - last);
            last = now;
            clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));
            Thread.Sleep(5);
        }
    }
}
