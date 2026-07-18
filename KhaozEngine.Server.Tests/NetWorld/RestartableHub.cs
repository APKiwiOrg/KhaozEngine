using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A test harness for reconnect: holds the "current" <see cref="InMemoryHub"/>, hands a <see cref="KhaozEngine.NetWorld.WorldClient"/>
/// factory (<see cref="Connect"/>) that attaches a fresh client endpoint to whichever hub is current, and
/// <see cref="Restart"/> swaps in a brand-new hub (modelling a server process restart). The caller builds the
/// server over <see cref="ServerTransport"/> after each (re)start.
/// </summary>
public sealed class RestartableHub
{
    public InMemoryHub Current { get; private set; } = new();

    /// <summary>The current hub's server transport (hand to a fresh WorldServer/ShardedWorldServer).</summary>
    public INetTransport ServerTransport => Current.Server;

    /// <summary>A WorldClient transport factory: each call creates a client endpoint on the current hub.</summary>
    public System.Func<INetTransport> Connect => () => Current.CreateClient();

    /// <summary>Models a server restart: swaps in a new hub. The old endpoints stop receiving; the next
    /// <see cref="Connect"/> call (a reconnect attempt) attaches to the new hub.</summary>
    public void Restart() => Current = new InMemoryHub();
}
