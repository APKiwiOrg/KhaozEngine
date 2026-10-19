using System.Numerics;
using System.Threading;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Lazy traversal bounds, immutable during reads. The mesh-local lock never gates unrelated installations.</summary>
internal sealed class InstalledMeshSweepBoundsCache
{
    readonly object _gate = new();
    (Vector3 Min, Vector3 Max)[]? _published;

    // The world write gate excludes every query before a pose or tree proof changes.
    internal (Vector3 Min, Vector3 Max)[]? TakeForWrite()
    {
        var bounds = _published;
        Volatile.Write(ref _published, null);
        return bounds;
    }

    internal void Publish((Vector3 Min, Vector3 Max)[] bounds) => Volatile.Write(ref _published, bounds);

    internal (Vector3 Min, Vector3 Max)[]? Get(InstalledMeshSweepGeometry mesh)
    {
        var bounds = Volatile.Read(ref _published);
        if (bounds is not null) return bounds;
        lock (_gate)
        {
            bounds = _published;
            if (bounds is not null) return bounds;
            bounds = mesh.CreateSweepBounds();
            if (bounds is not null) Volatile.Write(ref _published, bounds);
            return bounds;
        }
    }
}
