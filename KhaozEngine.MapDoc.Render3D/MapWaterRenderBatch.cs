using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

internal sealed record MapWaterRenderPart(MapRenderKey Key, MapSubmissionAnchor Anchor, MapBox3 Bounds,
    MapWaterResource Payload, string Digest);

/// <summary>Immutable canonical inventory and device-free draw plan. Registration fixes one generation and
/// frames retain the caller's captured look, never the camera's level or a global sea fallback.</summary>
public sealed class MapWaterRenderBatch
{
    readonly IReadOnlyList<MapWaterRenderPart> _parts;
    readonly MapFrozenWaterLook _look;
    MapPreparedResource[]? _registered;
    MapResourceStamp? _stamp;
    public IReadOnlyList<MapWaterResourceCell> Cells { get; }
    public long RetainedBytes { get; }
    public long PreparationChargeBytes { get; }
    internal MapWaterRenderBatch(IReadOnlyList<MapWaterResourceCell> cells, IReadOnlyList<MapWaterRenderPart> parts,
        MapFrozenWaterLook look, long charge)
    {
        Cells = Array.AsReadOnly(cells.ToArray()); _parts = Array.AsReadOnly(parts.ToArray()); _look = look;
        PreparationChargeBytes = charge;
        RetainedBytes = checked(512L + cells.Sum(c => 256L + c.Regions.Sum(r => 128L + r.Triangles.Count * 144L)) +
            parts.Sum(p => 512L + ModelVertex.SizeInBytes * (long)p.Payload.BorrowMesh.Vertices.Length +
                4L * p.Payload.BorrowMesh.Indices32.Length + p.Payload.BorrowClipData.Length));
    }
    public void Register(MapResourceStamp stamp, IMapDrawSink sink)
    {
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(sink);
        if (_stamp is not null && _stamp != stamp) throw new InvalidOperationException("A water batch cannot change its registered generation.");
        _stamp ??= stamp;
        _registered ??= _parts.Select(p => new MapPreparedResource(new(p.Key, _stamp), p.Bounds, p.Payload, p.Digest)).ToArray();
        foreach (var resource in _registered) sink.Register(resource);
    }
    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_registered is null) throw new InvalidOperationException("Register water before recording frames.");
        for (int i = 0; i < _parts.Count; i++)
            sink.Write(new(_registered[i].Reference, MapDrawChannels.Colour, _parts[i].Anchor, Matrix4x4.Identity,
                MapRenderTransform.FocusLocal(frame.Focus, _parts[i].Anchor), null, null, new MapWaterFrame(_look), null));
    }
}
