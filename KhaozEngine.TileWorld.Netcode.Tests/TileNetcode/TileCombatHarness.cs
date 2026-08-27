using System;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A server and one client over the in-process transport, on ONE thread, with the client's command tick deliberately
/// phase-offset from the server's. The phase is the whole point: two heads stepping in lockstep hide every ordering
/// bug a real client's independent clock runs into, which is the lesson this package's loopback tests already
/// learned. Shared by the combat tasks so there is one harness rather than three.
/// <para>NEITHER an actor behaviour NOR combat rules are wired, which is the server's own default: an actor stands
/// exactly where a test puts it and nothing ever swings. A test that wants either sets it on <c>Server</c> after
/// construction.</para>
/// </summary>
internal sealed class TileCombatHarness : IDisposable
{
    public const float Tick = 0.25f;
    public const float Frame = 0.05f;

    public readonly TileWorldServer Server;
    public readonly TileWorldClient Client;
    readonly InMemoryTransportHub hub;
    readonly INetTransport clientTransport;
    float serverAccum;

    public TileCombatHarness(TileWorldDocument doc, TileCoord spawn, float clientPhase = 0.06f,
        TileWorldServerConfig? config = null)
    {
        hub = new InMemoryTransportHub();
        Server = new TileWorldServer(hub.Server, config ?? TileWorldServerTickTests.Config(spawn),
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator(),
            TileProtocol.CreateRegistry());
        clientTransport = hub.CreateClient();
        Client = new TileWorldClient(clientTransport, new TileWorldClientConfig
        {
            TickSeconds = Tick,
            StepTicks = new TileStepTicks(walk: 4, run: 2),
        }, TileMoveSimulatorTests.Bake(doc), registry: TileProtocol.CreateRegistry());
        Client.Tick(clientPhase);
        Client.Poll();
    }

    /// <summary>Drops the client's transport, which is how a real link dies. The server's own Disconnect is a no-op
    /// on this hub, so a kick alone never reaches the client as a dropped session.</summary>
    public void Drop() => hub.DisconnectClient(clientTransport);

    public void Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Client.Tick(Frame);
            Server.Poll();
            serverAccum += Frame;
            while (serverAccum >= Tick)
            {
                serverAccum -= Tick;
                Server.Tick(Tick);
            }
            Client.Poll();
            Client.AdvancePresentation(Frame);
        }
    }

    // The transport is the harness's, so the harness closes it. TileWorldClient deliberately leaves it alone (a head
    // may reconnect over the same one), which is why this is the only place it can be released.
    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        clientTransport.Dispose();
    }
}
