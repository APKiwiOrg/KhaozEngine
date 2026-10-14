using System;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServer
{
    /// <summary>Releases the owned movement binding. Injected transport and physics remain caller-owned.</summary>
    public void Dispose() => simulator.Dispose();
}
