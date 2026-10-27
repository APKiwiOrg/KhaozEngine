using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public sealed class MapRenderMaterial
{
    readonly GltfMaterialMaps _maps;
    public string ResourceId { get; }
    public Vector4 BaseColour { get; }
    public float Roughness { get; }
    public float Metallic { get; }
    public float AlphaCutoff { get; }
    public GltfMaterialMaps Maps => MapRenderCopies.Maps(_maps);
    internal GltfMaterialMaps BorrowMaps => _maps;
    public MapRenderMaterial(string resourceId, Vector4 baseColour, float roughness, float metallic, float alphaCutoff, GltfMaterialMaps maps)
    {
        ResourceId = resourceId; BaseColour = baseColour; Roughness = roughness;
        Metallic = metallic; AlphaCutoff = alphaCutoff; _maps = MapRenderCopies.Maps(maps);
    }
}

/// <summary>Decodes explicitly mapped terrain and paint materials under the same allowance as mesh tiers.</summary>
public static class MapRenderMaterials
{
    internal sealed class MaterialSet(Dictionary<string, MapRenderMaterial> values, GltfDecodeBudget budget, long charge)
        : ReadOnlyDictionary<string, MapRenderMaterial>(values), IDisposable
    {
        bool _disposed;
        internal long RetainedBytes => charge;
        public void Dispose() { if (_disposed) return; _disposed = true; budget.ReleaseOwned(charge); }
    }

    public static IReadOnlyDictionary<string, MapRenderMaterial> Load(MapAssetClosure closure,
        IReadOnlyList<string> materialIds, GltfDecodeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(closure); ArgumentNullException.ThrowIfNull(materialIds); ArgumentNullException.ThrowIfNull(budget);
        long owned = 0;
        int images = 0;
        try
        {
            // Covers the dictionary and published collection even when no textures or material rows are selected.
            const long collectionCharge = 256;
            MapRenderDecodeCapacity.Reserve(budget, collectionCharge, 0);
            owned = collectionCharge;
            var result = new Dictionary<string, MapRenderMaterial>(StringComparer.Ordinal);
            foreach (string id in materialIds.Distinct(StringComparer.Ordinal))
            {
                ArgumentNullException.ThrowIfNull(id);
                // Covers material/definition objects, UTF-16 identity storage and conservative dictionary growth.
                long materialCharge = checked(512L + 24 + 2L * id.Length);
                MapRenderDecodeCapacity.Reserve(budget, materialCharge, 0);
                owned = checked(owned + materialCharge);
                long before = owned;
                var definition = ReadDefinition(closure, id, budget);
                var source = closure.GetResource(id);
                var maps = new GltfMaterialMaps(Decode(definition.Albedo, source), Decode(definition.Normal, source),
                    Decode(definition.RoughnessMap, source), definition.AlphaCutoff);
                long copy = owned - before;
                MapRenderDecodeCapacity.Reserve(budget, 0, copy);
                try { result.Add(id, new(id, definition.BaseColour, definition.Roughness, definition.Metallic, definition.AlphaCutoff, maps)); }
                finally { budget.ReleaseTemporary(copy); }
            }
            var set = new MaterialSet(result, budget, owned); owned = 0;
            return set;
        }
        catch (ImageDecodeRefusal ex) { throw new MapRenderAssetRefusal(ImageStatus(ex.Status), ex.Message); }
        catch (MapDocumentException ex) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, ex.Message); }
        catch (OverflowException) { throw Capacity(); }
        finally { if (owned != 0) budget.ReleaseOwned(owned); }

        DecodedImage? Decode(string? id, MapResolvedResource parent)
        {
            if (id is null) return null;
            if (DemandDiagnostics.Check(budget.DemandIdentity, "MapRenderMaterials.MaxImages.1", "MaxImages", images, 1, budget.Limits.MaxImages, images >= budget.Limits.MaxImages, CapacityDemandLifetime.Transient)) throw MapRenderDecodeCapacity.Refuse(nameof(GltfDecodeLimits.MaxImages), (long)images + 1, budget.Limits.MaxImages);
            images++;
            if (!parent.Dependencies.Contains(id, StringComparer.Ordinal))
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Material texture is not a declared dependency.");
            var resource = closure.GetResource(id);
            if (resource.Kind != MapResourceKind.Material) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Material texture dependency has the wrong resource role.");
            MapRenderDecodeCapacity.Reserve(budget, 0, resource.ByteLength);
            try
            {
                var info = ImageDecodeAdmission.Probe(closure.BorrowVerifiedResourceBytes(id).Span);
                long pixels = checked((long)info.Width * info.Height);
                if (DemandDiagnostics.Check(budget.DemandIdentity, "MapRenderMaterials.MaxImagePixels.1", "MaxImagePixels", 0, pixels, budget.Limits.MaxImagePixels, pixels > budget.Limits.MaxImagePixels, CapacityDemandLifetime.Transient)) throw MapRenderDecodeCapacity.Refuse(nameof(GltfDecodeLimits.MaxImagePixels), pixels, budget.Limits.MaxImagePixels);
                if (DemandDiagnostics.Check(budget.DemandIdentity, "MapRenderMaterials.MaxImageBufferPixels.1", "MaxImageBufferPixels", 0, pixels, Array.MaxLength / 4, pixels > Array.MaxLength / 4, CapacityDemandLifetime.Transient)) throw MapRenderDecodeCapacity.Refuse("MaxImageBufferPixels", pixels, Array.MaxLength / 4);
                long charge = checked(pixels * 4 + 24), temporary = checked(pixels * 8 + resource.ByteLength * 2L + 256);
                MapRenderDecodeCapacity.Reserve(budget, charge, temporary);
                bool success = false;
                try
                {
                    var image = ImageRgba.DecodeBounded(resource.Bytes.Span, budget.Limits.MaxPeakBytes, budget.Limits.MaxImagePixels);
                    owned = checked(owned + charge); success = true;
                    return new(image.Pixels, image.Width, image.Height);
                }
                finally { budget.ReleaseTemporary(temporary); if (!success) budget.ReleaseOwned(charge); }
            }
            finally { budget.ReleaseTemporary(resource.ByteLength); }
        }
    }

    internal static MapRenderMaterialDefinition ReadDefinition(MapAssetClosure closure, string id, GltfDecodeBudget budget)
    {
        var resource = closure.GetResource(id);
        if (resource.Kind != MapResourceKind.Material) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Render material requires a Material resource.");
        MapRenderDecodeCapacity.Reserve(budget, 0, resource.ByteLength);
        long metadata = 0;
        try
        {
            var reader = new Utf8JsonReader(closure.BorrowVerifiedResourceBytes(id).Span);
            long tokens = 0;
            try { while (reader.Read()) tokens = checked(tokens + 1); }
            catch (JsonException) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Required native material is opaque."); }
            long demand = checked(512L + 2L * resource.ByteLength + 32 * tokens);
            MapRenderDecodeCapacity.Reserve(budget, 0, demand);
            metadata = demand;
            return MapRenderMaterialJson.Read(resource.Bytes, id);
        }
        finally { budget.ReleaseTemporary(resource.ByteLength + metadata); }
    }
    static MapRenderAssetStatus ImageStatus(ImageDecodeStatus status) => status switch
    { ImageDecodeStatus.CapacityExceeded => MapRenderAssetStatus.CapacityExceeded, ImageDecodeStatus.Unsupported => MapRenderAssetStatus.Unsupported, _ => MapRenderAssetStatus.Invalid };
    static MapRenderAssetRefusal Capacity() => new(MapRenderAssetStatus.CapacityExceeded, "Required material exceeds the remaining decode allowance.");
}
