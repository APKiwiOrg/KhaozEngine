using System;
using System.Reflection;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A server and one client over the in-process transport, on ONE thread, with the client's command tick
/// phase-offset from the server's. Lifted out of <see cref="TileWorldClientLoopbackTests"/> so the steering suite
/// reads the same two heads rather than growing a second harness beside it.
/// </summary>
internal sealed class TileLoopbackHarness : IDisposable
{
    public const float Tick = 0.25f;
    public const float Frame = 0.05f;
    public static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    public readonly TileWorldServer Server;
    public readonly TileWorldClient Client;
    readonly InMemoryTransportHub hub;
    readonly INetTransport clientTransport;
    float serverAccum;

    // goalRadius is handed to BOTH heads, because that is the contract: the client mirrors the server's own
    // refusal of an out-of-range goal, and a test that set it on one head would be testing the mismatch.
    // gameComponents is handed to BOTH registries for the same reason goalRadius goes to both heads: a game
    // component registered on one side only is skipped on the way in, silently, which is the whole of #700.
    // canRun is the SERVER's run gate and has no client half on purpose: the gate is authority the client does not
    // hold, so a test that wires it is testing the downgrade the client has to be corrected onto.
    public TileLoopbackHarness(TileWorldDocument serverDoc, TileWorldDocument clientDoc, TileCoord spawn,
        float clientPhase, int goalRadius = TilePathfinder.DefaultMaxRadius,
        Action<ReplicationRegistry>? gameComponents = null, ushort allocatorNode = 0, Func<int, bool>? canRun = null)
    {
        hub = new InMemoryTransportHub();
        Server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(spawn) with { MaxGoalRadius = goalRadius, CanRun = canRun },
            TileMoveSimulatorTests.Bake(serverDoc),
            new TileDocumentTargets(serverDoc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator(),
            TileProtocol.CreateRegistry(gameComponents));
        if (allocatorNode != 0)
        {
            FieldInfo allocator = typeof(TileWorldServer).GetField("allocator",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            allocator.SetValue(Server, new NetIdAllocator(allocatorNode));
        }
        clientTransport = hub.CreateClient();
        Client = new TileWorldClient(clientTransport, new TileWorldClientConfig
        {
            TickSeconds = Tick,
            StepTicks = Ticks,
            MaxGoalRadius = goalRadius,
        }, TileMoveSimulatorTests.Bake(clientDoc), registry: TileProtocol.CreateRegistry(gameComponents));
        // Phase the client's command tick off the server's, which is the loopback lesson: two hosts stepping
        // in lockstep hide every ordering bug a real client's independent clock exposes.
        Client.Tick(clientPhase);
        Client.Poll();
    }

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

    /// <summary>Drops the client's transport, which is how a real link dies. A server-side kick now reaches
    /// the client on its own (the hub's endpoints route Disconnect through DisconnectClient), so this is the
    /// CLIENT-side drop and an idempotent no-op after a kick has already taken the link.</summary>
    public void Drop() => hub.DisconnectClient(clientTransport);

    public void Dispose() { Client.Dispose(); Server.Dispose(); }
}
