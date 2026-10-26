using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

// A CPU preparation travels with its ground request, never with the completion-time document. Optical
// fields are built later, from the completed candidate and accepted mesh snapshots of one publication group.
// An uncertified preparation was captured under a refused displacement. A latched preparation carries the kept
// refusal of its unchanged field attempt. Either publishes terrain only, and its water becomes Refusal.
internal sealed record TileWaterPreparation(WaterPlane[] Planes, TileWaterSourceStamp Stamp, float Padding, WaterLook? Look,
    bool Uncertified = false, TileWaterOpticsFailure? Refusal = null);

/// <summary>Document provenance of one preparation. Tracked entries own their registry epochs while the stamp is
/// referenced by a resident generation, pending request or refusal. Availability identities also detect source
/// changes made between stream notification and preparation.</summary>
internal sealed class TileWaterSourceStamp
{
    internal readonly record struct Entry(TileWaterOpticsDependency Dependency, long Epoch, TileRegion? Region,
        byte Availability);
    readonly Entry[] _entries;
    readonly float _tileSize, _planeHeight;

    /// <summary>A null registry is the untracked default policy. It records document scale only.</summary>
    internal TileWaterSourceStamp(TileWorldDocument document, IEnumerable<TileWaterOpticsDependency> dependencies,
        TileWaterDependencyRegistry? registry)
    {
        _tileSize = document.TileSize;
        _planeHeight = document.PlaneHeight;
        var entries = new List<Entry>();
        if (registry is not null)
            foreach (TileWaterOpticsDependency dependency in dependencies)
                entries.Add(new Entry(dependency, registry.Acquire(dependency), document.GetRegion(dependency.Region),
                    TileGroundHeightWitness.Availability(document, dependency.Region)));
        _entries = entries.ToArray();
    }

    internal ReadOnlySpan<Entry> Entries => _entries;

    internal bool IsCurrent(TileWorldDocument document, TileWaterDependencyRegistry? registry)
    {
        if (_tileSize != document.TileSize || _planeHeight != document.PlaneHeight) return false;
        foreach (Entry entry in _entries)
            if (registry is null || !registry.IsCurrent(entry.Dependency, entry.Epoch) ||
                (entry.Dependency.Plane is null &&
                    (entry.Availability != TileGroundHeightWitness.Availability(document, entry.Dependency.Region) ||
                     !ReferenceEquals(entry.Region, document.GetRegion(entry.Dependency.Region)))))
                return false;
        return true;
    }
}

// Owns each unique reference-level field once, regardless of the number of row-run submissions.
internal sealed class TileWaterGeneration : IDisposable
{
    readonly ITileWorldWaterTriangleOpticsScene? _scene;
    readonly List<WaterOpticsHandle> _handles;
    readonly int[] _planeFields;
    internal TileWaterPreparation Preparation { get; }
    internal TileWaterTriangleBuild[] Fields { get; }
    // Identities only. A generation never retains another region's surface payload.
    internal TileGroundSurfaceId[] Sources { get; }
    internal TileWaterSourceStamp? Availability { get; }
    internal WaterPlane[] Planes { get; private set; }
    internal WaterLook? Look { get; private set; }
    internal float Padding => Preparation.Padding;
    bool _disposed;

    internal TileWaterGeneration(TileWaterPreparation preparation, TileWaterTriangleOutcome outcome,
        ITileWorldWaterTriangleOpticsScene? scene)
    {
        Preparation = preparation;
        Fields = outcome.Fields;
        Sources = outcome.Sources;
        Availability = outcome.Availability;
        _handles = new List<WaterOpticsHandle>(Fields.Length);
        _scene = scene;
        Planes = (WaterPlane[])preparation.Planes.Clone();
        _planeFields = new int[Planes.Length];
        Look = preparation.Look;
        try
        {
            foreach (TileWaterTriangleBuild field in Fields)
            {
                try { _handles.Add(scene!.LoadWaterOpticsTriangles(field.Field)); }
                catch (WaterOpticsSurfaceException refused) { throw new TileWaterFieldRefusedException(field, refused); }
            }
            for (int i = 0; i < Planes.Length; i++)
            {
                _planeFields[i] = -1;
                for (int f = 0; f < Fields.Length; f++)
                    if (Planes[i].SurfaceY == Fields[f].Level)
                    {
                        TileWaterTriangleBuild field = Fields[f];
                        Planes[i] = Planes[i].WithOptics(new WaterOpticsBinding(_handles[f], field.Origin, field.Field.Size));
                        _planeFields[i] = f;
                        break;
                    }
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

    // A failed wider preparation can retain this generation only if every displaced rectangle still fits one
    // immutable certificate of its field, the same containment the scene enforces at submission.
    internal bool Covers(float padding)
    {
        for (int i = 0; i < Planes.Length; i++)
        {
            WaterPlane p = Planes[i];
            if (!p.Optics.IsBound) continue;
            TileWaterTriangleBuild field = Fields[_planeFields[i]];
            double minX = (double)p.CenterX - p.HalfExtentX - padding - field.Origin.X;
            double minZ = (double)p.CenterZ - p.HalfExtentZ - padding - field.Origin.Z;
            double maxX = (double)p.CenterX + p.HalfExtentX + padding - field.Origin.X;
            double maxZ = (double)p.CenterZ + p.HalfExtentZ + padding - field.Origin.Z;
            bool inside = false;
            foreach (WaterOpticsRectangle certificate in field.Field.CertifiedRectangles)
                if (minX >= certificate.Minimum.X && minZ >= certificate.Minimum.Y &&
                    maxX <= certificate.Maximum.X && maxZ <= certificate.Maximum.Y)
                {
                    inside = true;
                    break;
                }
            if (!inside) return false;
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

/// <summary>The backend refused one field's scalars before allocating. Names that field, so the refusal reports
/// its own reference level and domain.</summary>
internal sealed class TileWaterFieldRefusedException(TileWaterTriangleBuild field, WaterOpticsSurfaceException refused)
    : Exception(refused.Message, refused)
{
    internal TileWaterTriangleBuild Field { get; } = field;
    internal WaterOpticsSurfaceFailure Reason { get; } = refused.Reason;
}
