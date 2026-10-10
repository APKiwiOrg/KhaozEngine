using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Support;

public sealed record MapSupportRequest(MapFramePoint Point, string? SurfaceId, string? SpaceId,
    MapFaceKey? CurrentSupport, float MaxStepUp, float MaxDropDown, float? MaxSlopeRadians);

public enum MapSupportStatus { Supported, LegacyFallback, NoSupport, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }

/// <summary>Support facts from one immutable acquisition, with explicit non-capture compatibility provenance.</summary>
public sealed class MapSupportResult
{
    public MapSupportStatus Status { get; }
    public MapFaceKey? Face { get; }
    public float WorldY { get; }
    public Vector3? Normal { get; }
    public bool IsCaptureSupport { get; }
    public MapLegacyCellTag? Compatibility { get; }
    public string? SpaceId { get; }
    public MapRecordRef? Via { get; }
    public string? Detail { get; }
    public MapReadWitness Witness { get; }

    internal MapSupportResult(MapSupportStatus status, MapFaceKey? face, float worldY, Vector3? normal,
        MapLegacyCellTag? compatibility, string? spaceId, MapRecordRef? via, string? detail, MapReadWitness witness)
    {
        Status = status;
        Face = face;
        WorldY = worldY;
        Normal = normal;
        IsCaptureSupport = status == MapSupportStatus.Supported;
        Compatibility = compatibility;
        SpaceId = spaceId;
        Via = via;
        Detail = detail;
        Witness = witness;
    }
}

public readonly record struct MapSupportCandidate(MapFaceKey Face, string SpaceId, float WorldY,
    Vector3 Normal, MapRecordRef? Via);

/// <summary>A caller-buffer outcome. A capacity refusal never certifies the partial buffer as usable.</summary>
public sealed class MapSupportCandidateSet
{
    public MapSupportStatus Status { get; }
    public int Count { get; }
    public int RequiredCapacity { get; }
    public MapReadWitness Witness { get; }

    internal MapSupportCandidateSet(MapSupportStatus status, int count, int requiredCapacity, MapReadWitness witness)
    {
        Status = status;
        Count = count;
        RequiredCapacity = requiredCapacity;
        Witness = witness;
    }
}

public sealed record MapPlacementSupport(string PlacementId, MapSupportResult Support);

/// <summary>A native placement snapshot together with its authored support provenance.</summary>
public sealed class MapSupportedResolution
{
    public MapResolvedDocument Document { get; }
    public IReadOnlyList<MapPlacementSupport> Supports { get; }

    internal MapSupportedResolution(MapResolvedDocument document, IEnumerable<MapPlacementSupport> supports)
    {
        Document = document;
        Supports = Array.AsReadOnly(supports.ToArray());
    }
}
