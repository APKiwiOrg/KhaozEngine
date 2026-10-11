using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

public enum MapWaterMediumKind : byte { Water }

/// <summary>A named water medium. <see cref="WadeSpeedScale"/> lies in (0, 1].</summary>
public sealed record MapWaterMediumDoc(string Key, MapWaterMediumKind Kind, float WadeSpeedScale);

/// <summary>A lattice cell rectangle that includes its minimum corner and excludes its maximum corner. Its corners
/// are <see cref="MapLatticeAddress.Corner"/> addresses in the domain frame.</summary>
public readonly record struct MapWaterRect(long MinX, long MinZ, long MaxX, long MaxZ);

/// <summary>One closed polygon ring of lattice addresses in the domain frame.</summary>
public sealed record MapWaterRing(IReadOnlyList<MapLatticeAddress> Vertices);

/// <summary>The XZ extent of a body: the union of its rects and rings in one lattice frame.</summary>
public sealed record MapWaterDomainDoc(MapLatticeFrame Frame, IReadOnlyList<MapWaterRect> Rects, IReadOnlyList<MapWaterRing> Rings);

/// <summary>One water body at one level. It is wet only inside the authored spaces it covers.
/// <see cref="RenderMaterial"/> names a <c>Material</c> resource in the document's asset closure.</summary>
public sealed record MapWaterBodyDoc(string Id, float SurfaceY, string Medium, string RenderMaterial,
    MapWaterDomainDoc Domain, IReadOnlyList<MapRecordRef> Covers, string? DisplayName = null);

/// <summary>The optional root <c>water</c> section. A present set, even an empty one, means bounded water: the
/// world is dry wherever no body covers.</summary>
public sealed class MapWaterSet
{
    public List<MapWaterMediumDoc> Media { get; } = new();
    public List<MapWaterBodyDoc> Bodies { get; } = new();

    /// <summary>A deep copy whose nested collections are fresh arrays, so later edits to this set never show
    /// through.</summary>
    public MapWaterSet Clone()
    {
        var copy = new MapWaterSet();
        copy.Media.AddRange(Media);
        copy.Bodies.AddRange(Bodies.Select(CloneBody));
        return copy;
    }

    /// <summary>SHA-256 hex over <c>kemap/water-set/1</c> and the canonical codec bytes.</summary>
    public string Digest() => MapWaterJson.Hash("kemap/water-set/1", w => MapWaterJson.Write(w, this));

    /// <summary>SHA-256 hex over <c>kemap/water-body-doc/1</c> and the body's canonical bytes.</summary>
    public static string BodyDigest(MapWaterBodyDoc body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return MapWaterJson.Hash("kemap/water-body-doc/1", w => MapWaterJson.WriteBody(w, body));
    }

    /// <summary>SHA-256 hex over <c>kemap/water-medium/1</c> and the medium's canonical bytes.</summary>
    public static string MediumDigest(MapWaterMediumDoc medium)
    {
        ArgumentNullException.ThrowIfNull(medium);
        return MapWaterJson.Hash("kemap/water-medium/1", w => MapWaterJson.WriteMedium(w, medium));
    }

    // Tolerates the null members validation reports, so a copy of an invalid set still validates the same way.
    static MapWaterBodyDoc CloneBody(MapWaterBodyDoc body) => body is null ? null! : body with
    {
        Domain = body.Domain is null ? null! : body.Domain with
        {
            Rects = Copy(body.Domain.Rects),
            Rings = Copy(body.Domain.Rings)?.Select(r => r is null ? null! : r with { Vertices = Copy(r.Vertices) }).ToArray()!,
        },
        Covers = Copy(body.Covers),
    };

    static T[] Copy<T>(IReadOnlyList<T> items) => items?.ToArray()!;
}
