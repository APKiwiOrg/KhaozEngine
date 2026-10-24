using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

// A CPU preparation travels with its ground request, never with the completion-time document.
// Availability identities also detect source changes made between stream notification and preparation.
internal sealed record TileWaterPreparation(WaterPlane[] Planes, TileWaterOpticsBuild[] Fields,
    TileWaterSourceStamp Stamp, float Padding, WaterLook? Look);

internal sealed class TileWaterSourceStamp
{
    internal readonly record struct Entry(TileWaterOpticsDependency Dependency, long Epoch, TileRegion? Region);
    readonly Entry[] _entries;
    readonly float _tileSize, _planeHeight;

    internal TileWaterSourceStamp(TileWorldDocument document, IEnumerable<TileWaterOpticsDependency> dependencies,
        IReadOnlyDictionary<TileWaterOpticsDependency, long> epochs)
    {
        _tileSize = document.TileSize;
        _planeHeight = document.PlaneHeight;
        var entries = new List<Entry>();
        foreach (TileWaterOpticsDependency dependency in dependencies)
            entries.Add(new Entry(dependency, epochs.GetValueOrDefault(dependency),
                document.GetRegion(dependency.Region)));
        _entries = entries.ToArray();
    }

    internal bool IsCurrent(TileWorldDocument document, IReadOnlyDictionary<TileWaterOpticsDependency, long> epochs)
    {
        if (_tileSize != document.TileSize || _planeHeight != document.PlaneHeight) return false;
        foreach (Entry entry in _entries)
            if (entry.Epoch != epochs.GetValueOrDefault(entry.Dependency) ||
                (entry.Dependency.Plane is null && !ReferenceEquals(entry.Region, document.GetRegion(entry.Dependency.Region))))
                return false;
        return true;
    }
}

// Owns each unique reference-level field once, regardless of the number of row-run submissions.
internal sealed class TileWaterGeneration : IDisposable
{
    readonly ITileWorldWaterOpticsScene? _scene;
    readonly List<WaterOpticsHandle> _handles;
    internal TileWaterPreparation Preparation { get; }
    internal WaterPlane[] Planes { get; private set; }
    internal WaterLook? Look { get; private set; }
    internal float Padding => Preparation.Padding;
    bool _disposed;

    internal TileWaterGeneration(TileWaterPreparation preparation, ITileWorldWaterOpticsScene? scene)
    {
        Preparation = preparation;
        _handles = new List<WaterOpticsHandle>(preparation.Fields.Length);
        _scene = scene;
        Planes = (WaterPlane[])preparation.Planes.Clone();
        Look = preparation.Look;
        try
        {
            foreach (TileWaterOpticsBuild field in preparation.Fields)
                _handles.Add(scene!.LoadWaterOpticsField(field.Field));
            for (int i = 0; i < Planes.Length; i++)
                for (int f = 0; f < preparation.Fields.Length; f++)
                    if (Planes[i].SurfaceY == preparation.Fields[f].Origin.Y)
                    {
                        TileWaterOpticsBuild field = preparation.Fields[f];
                        Planes[i] = Planes[i].WithOptics(new WaterOpticsBinding(_handles[f], field.Origin, field.Size));
                        break;
                    }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void SetLook(WaterLook? look)
    {
        if (ReferenceEquals(look, Look)) return;
        for (int i = 0; i < Planes.Length; i++)
        {
            WaterPlane p = Planes[i];
            Planes[i] = new WaterPlane(p.CenterX, p.SurfaceY, p.CenterZ, p.HalfExtentX, p.HalfExtentZ, look, p.Optics);
        }
        Look = look;
    }

    // A failed wider preparation can retain this generation only if its existing complete field
    // already covers the effective displacement plus interpolation support.
    internal bool Covers(float padding, float tileSize)
    {
        foreach (WaterPlane p in Planes)
        {
            WaterOpticsBinding b = p.Optics;
            if (!b.IsBound) continue;
            float reach = padding + tileSize;
            if (p.CenterX - p.HalfExtentX - reach < b.Origin.X ||
                p.CenterZ - p.HalfExtentZ - reach < b.Origin.Z ||
                p.CenterX + p.HalfExtentX + reach > b.Origin.X + b.Size.X ||
                p.CenterZ + p.HalfExtentZ + reach > b.Origin.Z + b.Size.Y) return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (WaterOpticsHandle handle in _handles) _scene!.UnloadWaterOpticsField(handle);
        _handles.Clear();
    }
}
