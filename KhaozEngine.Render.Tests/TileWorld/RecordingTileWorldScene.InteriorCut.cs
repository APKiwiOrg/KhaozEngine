using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

public sealed partial class RecordingTileWorldScene
{
    public InteriorCutFrame InteriorCutFrame { get; set; }
    public InteriorHeightField? InteriorCut { get; private set; }
    public List<InteriorHeightField?> InteriorPublications { get; } = new();
    public Action<InteriorHeightField?>? OnInteriorPublication { get; set; }
    public List<(MeshHandle Mesh, bool Complement, bool Cut)> InteriorOutlineParts { get; } = new();
    public List<(MeshHandle Mesh, bool Cut)> InteriorSilhouettes { get; } = new();

    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world,
        float dissolve, bool complement, bool cutInsideInteriors)
    {
        InteriorOutlineParts.Add((mesh, complement, cutInsideInteriors));
        DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    }

    public void DrawMeshSilhouette(MeshHandle mesh, Matrix4x4 world, Color color, float widthMetres,
        bool cutInsideInteriors)
    {
        InteriorSilhouettes.Add((mesh, cutInsideInteriors));
        DrawMeshSilhouette(mesh, world, color, widthMetres);
    }

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
