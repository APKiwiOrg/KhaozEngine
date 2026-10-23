using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record SceneSourceLight(long SourceObjectId, string SourceProfileId, Vector3 Offset, Vector3 Colour,
    float Intensity, float Radius, bool CastShadows)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapLightShadowExclusion? ShadowExclusion { get; init; }
}

/// <summary>Actual caller options, independently instantiated for the legacy and native heads.</summary>
internal sealed record ShippedScenePolicy(MapPlacementPolicy Placements, IReadOnlySet<string> AnimatedAssets,
    GroundCoverRenderOptions Foliage, WaterLook Water, IReadOnlyList<SceneSourceLight> Lights, IReadOnlyDictionary<string, MapFoliageModelExtent> ModelExtents)
{
    internal static readonly JsonSerializerOptions Json = Options();
    static JsonSerializerOptions Options()
    {
        var options = MapJsonTextOptions.CreateSerializer(false);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.IncludeFields = true;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ColourConverter());
        options.Converters.Add(new MotionConverter());
        return options;
    }
    internal static ShippedScenePolicy Read(JsonElement root)
    {
        UniqueMembers(root); Shape(root, "placements", "animatedAssets", "foliage", "water", "lights", "modelExtents");
        var placement = root.GetProperty("placements"); Shape(placement, "layers", "defaultDrawRadius");
        var layers = placement.GetProperty("layers").EnumerateArray().Select(row =>
        {
            Shape(row, "id", "assetIds", "lodDistances", "lodCrossfadeWidth", "drawRadius", "hlodDistance", "hlodCrossfadeWidth", "hlodWeldCell", "castsShadows");
            float N(string name) => checked((float)ShippedSceneManifest.Number(row, name));
            return new MapPlacementLayer(Text(row, "id"), ShippedSceneManifest.Strings(row.GetProperty("assetIds")).ToHashSet(StringComparer.Ordinal),
                row.GetProperty("lodDistances").EnumerateArray().Select(n => n.GetSingle()).ToArray(), N("lodCrossfadeWidth"), N("drawRadius"),
                N("hlodDistance"), N("hlodCrossfadeWidth"), N("hlodWeldCell"), row.GetProperty("castsShadows").GetBoolean());
        }).ToArray();
        var foliage = Closed<GroundCoverRenderOptions>(root.GetProperty("foliage"));
        var water = Closed<WaterLook>(root.GetProperty("water"));
        var lights = root.GetProperty("lights").EnumerateArray().Select(row =>
        {
            string[] fields = ["sourceObjectId", "sourceProfileId", "offset", "colour", "intensity", "radius", "castShadows"];
            if (row.TryGetProperty("shadowExclusion", out _)) fields = [.. fields, "shadowExclusion"];
            Shape(row, fields);
            Require(row.GetProperty("sourceObjectId").TryGetInt64(out long id) && id > 0, "invalid scene source light");
            Vector3 Vector(string name)
            { var v = row.GetProperty(name); Shape(v, "x", "y", "z"); return new((float)ShippedSceneManifest.Number(v, "x"), (float)ShippedSceneManifest.Number(v, "y"), (float)ShippedSceneManifest.Number(v, "z")); }
            float intensity = row.GetProperty("intensity").GetSingle(), radius = row.GetProperty("radius").GetSingle();
            Require(float.IsFinite(intensity) && intensity >= 0 && float.IsFinite(radius) && radius > 0, "invalid scene source light");
            return new SceneSourceLight(id, Text(row, "sourceProfileId"), Vector("offset"), Vector("colour"), intensity, radius, row.GetProperty("castShadows").GetBoolean())
            { ShadowExclusion = ShippedSceneLightShadows.Read(row) };
        }).ToArray();
        Require(lights.Select(l => (l.SourceObjectId, l.SourceProfileId)).Distinct().Count() == lights.Length, "duplicate scene source light");
        var extents = root.GetProperty("modelExtents").EnumerateArray().Select(row =>
        {
            Shape(row, "assetId", "boundsInSourceUnits", "sourceUnitsToMetres", "motionPaddingMetres");
            var bounds = row.GetProperty("boundsInSourceUnits"); Shape(bounds, "min", "max");
            Shape(bounds.GetProperty("min"), "x", "y", "z"); Shape(bounds.GetProperty("max"), "x", "y", "z");
            return row.Deserialize<MapFoliageModelExtent>(Json) ?? throw new System.IO.InvalidDataException("invalid scene model extent");
        }).ToDictionary(e => e.AssetId, StringComparer.Ordinal);
        _ = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(foliage), extents);
        return new(new(layers, placement.GetProperty("defaultDrawRadius").GetSingle()),
            ShippedSceneManifest.Strings(root.GetProperty("animatedAssets")).ToHashSet(StringComparer.Ordinal), foliage, water, Array.AsReadOnly(lights), extents);
    }
    // Property sets come from the actual released option type. Every member is required, including nullable options.
    static T Closed<T>(JsonElement value) where T : new()
    {
        var template = JsonSerializer.SerializeToElement(new T(), Json);
        ClosedObject(value, template);
        return value.Deserialize<T>(Json) ?? throw new System.IO.InvalidDataException("invalid scene policy");
    }
    static void ClosedObject(JsonElement value, JsonElement template)
    {
        if (template.ValueKind != JsonValueKind.Object) return;
        Shape(value, template.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (var member in template.EnumerateObject())
            if (member.Value.ValueKind == JsonValueKind.Object && !IsDictionary(member.Name)) ClosedObject(value.GetProperty(member.Name), member.Value);
    }
    static bool IsDictionary(string name) => name is "motionProfiles" or "modelCastsShadows";

    internal MapRenderPolicy Native(MapAssetClosure closure, ShippedSceneManifest manifest)
    {
        foreach (var extent in ModelExtents.Values)
        {
            var asset = closure.GetAsset(extent.AssetId);
            Require(extent.SourceUnitsToMetres == asset.SourceUnitsToMetres, "scene model extent units differ");
        }
        return new(Placements, AnimatedAssets, new(MapFrozenFoliageOptions.Capture(Foliage), ModelExtents), MapFrozenWaterLook.Capture(Water),
            manifest.Materials.Select(m => m.NativeResourceId).ToArray(), manifest.Materials.ToDictionary(m => m.SourceMaterialId, m => m.NativeResourceId));
    }
    sealed class ColourConverter : JsonConverter<KhaozEngine.Primitives.Color>
    {
        public override KhaozEngine.Primitives.Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader); var row = json.RootElement;
            Shape(row, "r", "g", "b", "a");
            float N(string name) { float value = row.GetProperty(name).GetSingle(); Require(float.IsFinite(value), "invalid scene colour"); return value; }
            return new(N("r"), N("g"), N("b"), N("a"));
        }
        public override void Write(Utf8JsonWriter writer, KhaozEngine.Primitives.Color value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteNumber("r", value.R); writer.WriteNumber("g", value.G); writer.WriteNumber("b", value.B); writer.WriteNumber("a", value.A); writer.WriteEndObject();
        }
    }
    sealed class MotionConverter : JsonConverter<FoliageMotionProfile>
    {
        public override FoliageMotionProfile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var json = JsonDocument.ParseValue(ref reader); var row = json.RootElement;
            Shape(row, "bendScale", "maxLean", "windScale", "shiver");
            return new(row.GetProperty("bendScale").GetSingle(), row.GetProperty("maxLean").GetSingle(), row.GetProperty("windScale").GetSingle(), row.GetProperty("shiver").GetSingle());
        }
        public override void Write(Utf8JsonWriter writer, FoliageMotionProfile value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteNumber("bendScale", value.BendScale); writer.WriteNumber("maxLean", value.MaxLean); writer.WriteNumber("windScale", value.WindScale); writer.WriteNumber("shiver", value.Shiver); writer.WriteEndObject();
        }
    }
}
