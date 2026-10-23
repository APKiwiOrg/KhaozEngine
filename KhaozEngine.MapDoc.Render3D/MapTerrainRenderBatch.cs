using System;
using KhaozEngine.MapDoc.Scene;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapTerrainCanonicalFace(MapCompiledFace Face, MapExactTriangle Triangle,
    MapPaintCoverage Paint, MapSubmissionAnchor Anchor);

/// <summary>Disjoint render coverage. Its polygon lies on Face's canonical triangle and creates no support face.</summary>
public sealed record MapTerrainPartition(MapFaceKey Face, string? PaintId, MapPaintCoverage Paint,
    IReadOnlyList<MapExactPoint> Polygon)
{
    public IReadOnlyList<MapExactPoint> Polygon { get; init; } = Array.AsReadOnly(Polygon.ToArray());
}

internal sealed record MapTerrainPart(MapRenderKey Key, MapSubmissionAnchor Anchor, MapBox3 Bounds,
    MapTerrainResource Payload, string Digest);

/// <summary>Immutable CPU terrain preparation. Keep the batch alive while using its registrations. Its input lease
/// retains decoded material ownership independently of the caller's original lease. RetainedBytes charges owned
/// geometry and provenance, InputRetainedBytes reports the shared decoded charge, and PreparationChargeBytes also
/// includes all charged intermediate compilation work.</summary>
public sealed class MapTerrainRenderBatch : IDisposable
{
    const MapDrawChannels TerrainChannels = MapDrawChannels.Colour | MapDrawChannels.Shadow | MapDrawChannels.Camera |
        MapDrawChannels.Sky | MapDrawChannels.Selection | MapDrawChannels.Physical;
    readonly MapPreparedInputLease _input;
    readonly IReadOnlyList<MapTerrainPart> _parts;
    MapPreparedResource[]? _registered;
    MapResourceStamp? _stamp;
    bool _disposed;
    public IReadOnlyList<MapTerrainCanonicalFace> CanonicalFaces { get; }
    public IReadOnlyList<MapTerrainPartition> Partitions { get; }
    public IReadOnlyList<MapTerrainFinding> Findings { get; }
    public long RetainedBytes { get; }
    public long InputRetainedBytes => _input.RetainedBytes;
    internal MapPreparationReservation? PreparationOwnership { get; private set; }
    internal void Own(MapPreparationReservation ownership)
    { PreparationOwnership = ownership; PreparationChargeBytes = ownership.Bytes; }
    public long PreparationChargeBytes { get; private set; }
    internal MapTerrainRenderBatch(MapPreparedInputLease input, IReadOnlyList<MapTerrainCanonicalFace> canonical,
        IReadOnlyList<MapTerrainPartition> partitions, IReadOnlyList<MapTerrainPart> parts,
        IReadOnlyList<MapTerrainFinding> findings, long charge)
    {
        _input = input.Retain(); _parts = Array.AsReadOnly(parts.ToArray());
        CanonicalFaces = Array.AsReadOnly(canonical.ToArray()); Partitions = Array.AsReadOnly(partitions.ToArray());
        Findings = Array.AsReadOnly(findings.ToArray()); PreparationChargeBytes = charge;
        RetainedBytes = checked(512L + canonical.Count * NativeMapTerrain.CanonicalRowBytes + partitions.Sum(p => 128L + 48L * p.Polygon.Count) +
            parts.Sum(p => 512L + ModelVertex.SizeInBytes * (long)p.Payload.BorrowMesh.Vertices.Length +
                4L * p.Payload.BorrowMesh.Indices32.Length + 80L * p.Payload.FaceKeys.Count) + 128L * findings.Count +
            parts.Select(p => p.Payload.Materials).Distinct().Sum(m => 128L + 64L * m.Layers.Count));
    }

    public void Register(MapResourceStamp stamp, IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(sink);
        if (_stamp is not null && _stamp != stamp) throw new InvalidOperationException("A terrain batch cannot change its registered generation.");
        _stamp ??= stamp;
        _registered ??= _parts.Select(p => new MapPreparedResource(new(p.Key, _stamp), p.Bounds, p.Payload, p.Digest)).ToArray();
        foreach (var resource in _registered) sink.Register(resource);
    }

    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sink);
        if (_registered is null) throw new InvalidOperationException("Register terrain before recording frames.");
        var rigid = new MapRigidFrame(Color.White, Material.None, 0, 0, default, false, 0, MotionKey.None);
        for (int i = 0; i < _parts.Count; i++)
        {
            var anchor = _parts[i].Anchor;
            sink.Write(new(_registered[i].Reference, TerrainChannels, anchor, Matrix4x4.Identity,
                MapRenderTransform.FocusLocal(frame.Focus, anchor), rigid, null, null, null));
        }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _input.Dispose(); PreparationOwnership?.Dispose(); }
}
