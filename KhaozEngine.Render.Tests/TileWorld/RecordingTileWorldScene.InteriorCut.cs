using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

public sealed partial class RecordingTileWorldScene
{
    public InteriorHeightField? InteriorCut { get; private set; }
    public List<InteriorHeightField?> InteriorPublications { get; } = new();
    public Action<InteriorHeightField?>? OnInteriorPublication { get; set; }

    public void SetInteriorCut(InteriorHeightField? field)
    {
        InteriorCut = field;
        InteriorPublications.Add(field);
        OnInteriorPublication?.Invoke(field);
    }
}

internal sealed class InteriorTestMeshes : ITileMeshGeometry
{
    public List<int> Threads { get; } = new();
    public bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh,
        [NotNullWhen(false)] out string? error)
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        mesh = null;
        error = "missing test roof";
        return false;
    }
}

internal static class InteriorTestWorker
{
    // Explicit worker completion, with no sleeps, polling or timing assertions.
    internal static void Run(ManualTileBuildDispatcher dispatcher) =>
        Task.Factory.StartNew(dispatcher.RunAll, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
}
