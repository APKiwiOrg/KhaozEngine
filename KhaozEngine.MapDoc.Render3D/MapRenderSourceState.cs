using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapRenderSourceState(MapSceneSource Source, MapAssetClosure Closure,
    MapAssetExtentProofSet Extents, MapRenderPolicy Policy, IReadOnlyList<MapSceneScope> Windows)
{
    public IReadOnlyList<MapSceneScope> Windows { get; init; } = Array.AsReadOnly(Windows.ToArray());
}

public sealed record MapRenderTransition(long Epoch, MapSceneStatus Status, MapSceneWitness? AcceptedWitness, MapSceneWitness? PendingWitness)
{
    public MapSceneCapacity? Capacity { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}
public sealed record MapRenderReadiness(MapRenderTransition Transition, int Pending, int Ready, long CpuBytes, long GpuBytes, long QueuedBytes);
