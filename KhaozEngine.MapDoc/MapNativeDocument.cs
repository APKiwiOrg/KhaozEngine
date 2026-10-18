using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc;

public sealed partial class MapDocument
{
    /// <summary>The playable XZ extent, contained in storage Bounds. Null leaves playability unspecified.</summary>
    [JsonPropertyOrder(MapDocumentOrder.PlayableBounds)]
    public MapBounds? PlayableBounds { get; set; }

    /// <summary>Digest-bearing references to native asset payloads. Empty for legacy analytic maps.</summary>
    [JsonPropertyOrder(MapDocumentOrder.NativeAssets)]
    public List<MapAssetRef> NativeAssets { get; set; } = new();

    /// <summary>The highest reserved numeric placement identity, including deleted placements.</summary>
    [JsonConverter(typeof(MapNumericIdJsonConverter))]
    [JsonPropertyOrder(MapDocumentOrder.NumericIdHighWaterMark)]
    public long NumericIdHighWaterMark { get; set; }

    /// <summary>Native resolver version metadata. Null retains the legacy analytic execution path.</summary>
    [JsonPropertyOrder(MapDocumentOrder.ResolverIdentity)]
    public MapResolverIdentityDoc? ResolverIdentity { get; set; }
}

public sealed partial class MapPlacement
{
    /// <summary>Optional game numeric identity, distinct from the stable editor Id and asset kind.</summary>
    [JsonConverter(typeof(MapNullableNumericIdJsonConverter))]
    [JsonPropertyOrder(MapPlacementOrder.NumericId)]
    public long? NumericId { get; set; }

    /// <summary>Optional native asset reference, distinct from the legacy Kind.</summary>
    [JsonPropertyOrder(MapPlacementOrder.AssetId)]
    public string? AssetId { get; set; }

    /// <summary>Optional display label. Renaming this label does not change placement identity.</summary>
    [JsonPropertyOrder(MapPlacementOrder.DisplayName)]
    public string? DisplayName { get; set; }
}

/// <summary>The versions governing the native resolver payload and algorithm.</summary>
public sealed record MapResolverIdentityDoc(int PayloadVersion, int ResolverVersion);

/// <summary>A named native asset payload and its expected content digest.</summary>
public sealed record MapAssetRef(string Id, string Path, string Sha256, int PayloadVersion);
