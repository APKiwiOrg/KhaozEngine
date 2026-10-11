using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapEdit;

public sealed record MapWaterSampleInfo(MapWaterStatus Status, string? BodyId, string? SpaceId,
    float LowerY, float UpperY, float NominalSurfaceY, bool UpperIsFreeSurface, string? Detail);

/// <summary>Read-only bounded water services over the session's open document.</summary>
public sealed class WaterService(MapEditSession session)
{
    /// <summary>Returns detached bodies in ordinal ID order. An absent water section has no bodies.</summary>
    public IReadOnlyList<MapWaterBodyDoc> List() => session.WithDocument((document, _) =>
        document.Water?.Clone().Bodies.OrderBy(body => body.Id, StringComparer.Ordinal).ToArray()
        ?? Array.Empty<MapWaterBodyDoc>());

    /// <summary>Returns a detached body by its exact ID, or refuses an unknown ID.</summary>
    public MapWaterBodyDoc Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return session.WithDocument((document, _) =>
            document.Water?.Clone().Bodies.Find(body => string.Equals(body.Id, id, StringComparison.Ordinal))
            ?? throw new MapDocumentException($"Water body '{id}' does not exist."));
    }

    /// <summary>Samples world metres through the native water query. An absent water section is dry.</summary>
    public MapWaterSampleInfo Sample(float x, float y, float z)
    {
        return session.WithDocument((document, _) =>
        {
            if (document.Water is null)
                return new MapWaterSampleInfo(MapWaterStatus.Dry, null, null,
                    float.NaN, float.NaN, float.NaN, false, null);
            MapScopedSurfaces view = MapScopedSurfaces.CompleteView(document.Surfaces);
            var point = new MapFramePoint(view.Scope.Frame, view.Scope.Frame.ToLocal(new Vector3(x, y, z)));
            MapWaterSample sample = new MapWaterQuery(view, document.Water).Sample(point);
            return new MapWaterSampleInfo(sample.Status, sample.BodyId, sample.SpaceId,
                sample.LowerY, sample.UpperY, sample.NominalSurfaceY, sample.UpperIsFreeSurface, sample.Detail);
        });
    }
}
