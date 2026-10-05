using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc;

/// <summary>A world transform using right-handed +Y yaw and positive uniform scale.</summary>
public readonly record struct MapTransform(Vector3 Position, float YawRadians, float Scale)
{
    public Vector3 TransformPoint(Vector3 local)
    {
        float c = MathF.Cos(YawRadians), s = MathF.Sin(YawRadians);
        return Position + new Vector3(c * local.X + s * local.Z, local.Y, -s * local.X + c * local.Z) * Scale;
    }

    public static MapTransform Compose(MapTransform parent, MapTransform local) =>
        new(parent.TransformPoint(local.Position), parent.YawRadians + local.YawRadians, parent.Scale * local.Scale);
}

/// <summary>A placement snapshot with distinct stable, kind, asset and optional game identities.</summary>
public sealed class MapResolvedPlacement
{
    public string PlacementId { get; }
    public string Kind { get; }
    public string AssetId { get; }
    public long? NumericId { get; }
    public MapTransform Transform { get; }
    public IReadOnlyList<string> Tags { get; }

    public MapResolvedPlacement(string placementId, string kind, string assetId, long? numericId,
        MapTransform transform, IReadOnlyList<string> tags)
    {
        PlacementId = placementId;
        Kind = kind;
        AssetId = assetId;
        NumericId = numericId;
        Transform = transform;
        Tags = Array.AsReadOnly(tags.ToArray());
    }
}

/// <summary>An immutable XZ extent, independent of mutable authoring bounds.</summary>
public readonly record struct MapResolvedBounds(float MinX, float MinZ, float MaxX, float MaxZ)
{
    internal static MapResolvedBounds Of(MapBounds bounds) => new(bounds.MinX, bounds.MinZ, bounds.MaxX, bounds.MaxZ);
}

/// <summary>The consumer's versioned build inputs, included in complete authored identity.</summary>
public sealed record MapResolveOptions(string BuilderId, int BuilderVersion, string OptionsHash, int ResolverVersion = 1);

/// <summary>A complete immutable native world snapshot. No partial document can publish this result.</summary>
public sealed class MapResolvedDocument
{
    public IReadOnlyList<MapResolvedPlacement> Placements { get; }
    public IReadOnlyList<MapResolvedAsset> Assets { get; }
    /// <summary>The verified closure this snapshot resolved against, for binding and resource lookup without a second load.</summary>
    public MapAssetClosure AssetClosure { get; }
    public MapResolvedBounds PlayableBounds { get; }
    public MapResolvedBounds StorageBounds { get; }
    public string AuthoredHash { get; }

    internal MapResolvedDocument(IEnumerable<MapResolvedPlacement> placements, MapAssetClosure assets,
        MapResolvedBounds playableBounds, MapResolvedBounds storageBounds, string authoredHash)
    {
        Placements = Array.AsReadOnly(placements.ToArray());
        Assets = assets.Assets;
        AssetClosure = assets;
        PlayableBounds = playableBounds;
        StorageBounds = storageBounds;
        AuthoredHash = authoredHash;
    }
}
