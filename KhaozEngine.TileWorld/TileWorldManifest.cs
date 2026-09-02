using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.TileWorld;

/// <summary>On-disk shape of <c>world.json</c>.</summary>
internal sealed class TileWorldManifest
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public int FormatVersion { get; set; }
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public float TileSize { get; set; } = TileWorldDocument.DefaultTileSize;
    public int PlaneCount { get; set; } = TileWorldDocument.DefaultPlaneCount;
    public float PlaneHeight { get; set; } = TileWorldDocument.DefaultPlaneHeight;
    public List<string> CatalogPaths { get; set; } = new();
    public long NextObjectId { get; set; } = 1;
    public List<TileWorldManifestRegion> Regions { get; set; } = new();
    // DERIVED from the regions, like the collision map: the regions stay the source of truth and this is the copy
    // a client can read before it has streamed any of them. Absent in a manifest written by an older engine, which
    // deserialises to an empty list and reads as a world with no markers rather than as a load failure.
    public List<TileWorldManifestMarker> Markers { get; set; } = new();
}

/// <summary>One marker index row, a copy of what its region carries so a lookup costs no region read.</summary>
internal sealed class TileWorldManifestMarker
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Z { get; set; }
    public int Plane { get; set; }
    public List<string>? Tags { get; set; }
}

/// <summary>One manifest row: a region coordinate and the hash of its file's exact bytes.</summary>
internal sealed class TileWorldManifestRegion
{
    public int Rx { get; set; }
    public int Rz { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>On-disk shape of one <c>regions/r_x_z.json</c>.</summary>
internal sealed class TileRegionFileDto
{
    public int Rx { get; set; }
    public int Rz { get; set; }
    public List<TileRegionPlaneDto?> Planes { get; set; } = new();
    public List<TileObjectDto> Objects { get; set; } = new();
    public List<TileMarkerDto> Markers { get; set; } = new();
}

/// <summary>On-disk shape of one plane's dense layers, each base64 or absent.</summary>
internal sealed class TileRegionPlaneDto
{
    public string? Heights { get; set; }
    public string? Underlay { get; set; }
    public string? Overlay { get; set; }
    public string? OverlayShape { get; set; }
    public string? OverlayRotation { get; set; }
    public string? Settings { get; set; }
}

/// <summary>On-disk shape of one placed object.</summary>
internal sealed class TileObjectDto
{
    public long Id { get; set; }
    public string ArchetypeId { get; set; } = "";
    public int X { get; set; }
    public int Z { get; set; }
    public int Plane { get; set; }
    public int Rotation { get; set; }
    public List<string>? Tags { get; set; }
}

/// <summary>On-disk shape of one named marker.</summary>
internal sealed class TileMarkerDto
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Z { get; set; }
    public int Plane { get; set; }
    public List<string>? Tags { get; set; }
}

/// <summary>Serializer options for the two file kinds. The region form is compact on purpose: its bytes ARE
/// its hash, and nobody hand-reads a base64 region file.</summary>
internal static class TileWorldJson
{
    public static readonly JsonSerializerOptions Manifest = Build(indented: true);
    public static readonly JsonSerializerOptions Region = Build(indented: false);

    static JsonSerializerOptions Build(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
