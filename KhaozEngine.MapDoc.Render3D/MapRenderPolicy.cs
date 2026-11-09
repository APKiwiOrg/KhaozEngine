using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Frozen style and material identities. Decoded images belong only to selected input leases.</summary>
public sealed class MapRenderPolicy
{
    public MapPlacementPolicy Placements { get; }
    public IReadOnlySet<string> AnimatedAssets { get; }
    public NativeFoliagePolicy Foliage { get; }
    public MapFrozenWaterLook Water { get; }
    public IReadOnlyList<string> MaterialResourceIds { get; }
    public IReadOnlyDictionary<ushort, string> TerrainMaterials { get; }
    public string Digest { get; }
    public MapRenderPolicy(MapPlacementPolicy Placements, IReadOnlySet<string> AnimatedAssets,
        NativeFoliagePolicy Foliage, MapFrozenWaterLook Water, IReadOnlyList<string> MaterialResourceIds,
        IReadOnlyDictionary<ushort, string> TerrainMaterials)
    {
        ArgumentNullException.ThrowIfNull(Placements); ArgumentNullException.ThrowIfNull(AnimatedAssets);
        ArgumentNullException.ThrowIfNull(Foliage); ArgumentNullException.ThrowIfNull(Water);
        ArgumentNullException.ThrowIfNull(MaterialResourceIds); ArgumentNullException.ThrowIfNull(TerrainMaterials);
        if (AnimatedAssets.Any(string.IsNullOrWhiteSpace) || MaterialResourceIds.Any(string.IsNullOrWhiteSpace) || TerrainMaterials.Values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Render policy requires nonempty resource identities.");
        this.Placements = Placements; this.AnimatedAssets = AnimatedAssets.ToFrozenSet(StringComparer.Ordinal);
        this.Foliage = Foliage; this.Water = Water;
        this.MaterialResourceIds = Array.AsReadOnly(MaterialResourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        this.TerrainMaterials = new ReadOnlyDictionary<ushort, string>(new Dictionary<ushort, string>(TerrainMaterials));
        Digest = MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/render-policy/1"); w.WriteStringValue(Placements.Digest);
            foreach (string id in this.AnimatedAssets.Order(StringComparer.Ordinal)) w.WriteStringValue(id);
            w.WriteStringValue(Foliage.Digest);
            Canonical(w, Foliage.Options);
            Canonical(w, Water);
            foreach (string id in this.MaterialResourceIds) w.WriteStringValue(id);
            foreach (var pair in this.TerrainMaterials.OrderBy(p => p.Key)) { w.WriteNumberValue(pair.Key); w.WriteStringValue(pair.Value); }
            w.WriteEndArray();
        });
    }
    static void Canonical<T>(Utf8JsonWriter writer, T value)
    {
        var options = MapJsonTextOptions.CreateSerializer(false); options.IncludeFields = true;
        Write(JsonSerializer.SerializeToElement(value, options));
        void Write(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var child in element.EnumerateArray()) Write(child); writer.WriteEndArray(); }
            else element.WriteTo(writer);
        }
    }
}
