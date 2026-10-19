using System;
using System.Buffers;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Temporary writer proof storage, shared by size through the pools rather than retained per world.</summary>
internal readonly struct InstalledMeshSweepProofScratch : IDisposable
{
    internal bool[] SeenNodes { get; }
    internal bool[] SeenLeaves { get; }
    internal int[] Stack { get; }
    internal int[] Counts { get; }
    internal (Vector3 Min, Vector3 Max)[] Bounds { get; }

    internal InstalledMeshSweepProofScratch(int nodes, int triangles)
    {
        SeenNodes = ArrayPool<bool>.Shared.Rent(nodes);
        SeenLeaves = ArrayPool<bool>.Shared.Rent(triangles);
        Stack = ArrayPool<int>.Shared.Rent(nodes + triangles);
        Counts = ArrayPool<int>.Shared.Rent(nodes);
        Bounds = ArrayPool<(Vector3, Vector3)>.Shared.Rent(nodes * 2);
        Array.Clear(SeenNodes); Array.Clear(SeenLeaves);
    }

    public void Dispose()
    {
        ArrayPool<bool>.Shared.Return(SeenNodes);
        ArrayPool<bool>.Shared.Return(SeenLeaves);
        ArrayPool<int>.Shared.Return(Stack);
        ArrayPool<int>.Shared.Return(Counts);
        ArrayPool<(Vector3, Vector3)>.Shared.Return(Bounds);
    }
}
