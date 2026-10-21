using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapEdit;

public sealed record NativePlacementLayer(string Id, string[] AssetIds, float[] LodDistances,
    float LodCrossfadeWidth, float DrawRadius, float HlodDistance, float HlodCrossfadeWidth, float HlodWeldCell, bool CastsShadows)
{
    internal MapPlacementLayer Freeze()
    {
        if (AssetIds.Distinct(StringComparer.Ordinal).Count() != AssetIds.Length) throw new ArgumentException("DuplicateAssetIdentity");
        return new(Id, AssetIds.ToHashSet(StringComparer.Ordinal), LodDistances, LodCrossfadeWidth, DrawRadius,
            HlodDistance, HlodCrossfadeWidth, HlodWeldCell, CastsShadows);
    }
}
public sealed record NativePlacementPolicy(NativePlacementLayer[] Layers, float DefaultDrawRadius);
public sealed record NativeFoliageExtent(string AssetId, Vector3 Min, Vector3 Max, float SourceUnitsToMetres, float MotionPaddingMetres);
public sealed record NativeFoliagePolicyFile(GroundCoverRenderOptions Options, Dictionary<string, NativeFoliageExtent> Extents);
public sealed record NativeRenderPolicy(NativePlacementPolicy Placements, string[] AnimatedAssets,
    NativeFoliagePolicyFile Foliage, WaterLook Water, string[] MaterialResourceIds, Dictionary<ushort, string> TerrainMaterials)
{
    public MapRenderPolicy Freeze()
    {
        if (AnimatedAssets.Distinct(StringComparer.Ordinal).Count() != AnimatedAssets.Length || MaterialResourceIds.Distinct(StringComparer.Ordinal).Count() != MaterialResourceIds.Length ||
            Foliage.Extents.Any(p => p.Key != p.Value.AssetId)) throw new ArgumentException("InvalidRenderIdentities");
        ValidateWater(Water);
        foreach (var extent in Foliage.Extents.Values)
            if (string.IsNullOrWhiteSpace(extent.AssetId) || extent.Min.X > extent.Max.X || extent.Min.Y > extent.Max.Y || extent.Min.Z > extent.Max.Z ||
                extent.SourceUnitsToMetres <= 0 || extent.MotionPaddingMetres < 0) throw new ArgumentException("InvalidFoliageExtent");
        return new(new(Placements.Layers.Select(l => l.Freeze()).ToArray(), Placements.DefaultDrawRadius),
            AnimatedAssets.ToHashSet(StringComparer.Ordinal), new(MapFrozenFoliageOptions.Capture(Foliage.Options),
                Foliage.Extents.ToDictionary(p => p.Key, p => new MapFoliageModelExtent(p.Value.AssetId, new(p.Value.Min, p.Value.Max),
                    p.Value.SourceUnitsToMetres, p.Value.MotionPaddingMetres), StringComparer.Ordinal)),
            MapFrozenWaterLook.Capture(Water), MaterialResourceIds, TerrainMaterials);
    }
    static void ValidateWater(WaterLook water)
    {
        foreach (var field in typeof(WaterLook).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (field.GetValue(water) is float value && field.Name is not (nameof(WaterLook.SwellDirectionDegrees) or nameof(WaterLook.SwellSeed) or nameof(WaterLook.RippleSeed)) && value < 0)
                throw new ArgumentException("InvalidWaterRange");
        }
        if (water.Opacity is < 0 or > 1 || water.SwellSteepness is < 0 or > 1 || water.FoamCrestCoverage is < 0 or > 1 ||
            water.SwellComponents is < 1 or > 8 || water.RippleComponents is < 1 or > 12 || water.SwellWavelength is <= 0 ||
            water.WaveScale is <= 0 || water.FoamPatternScale is <= 0) throw new ArgumentException("InvalidWaterRange");
    }
}
public sealed record NativeLighting(Vector3 DirectionToLight, Color SunColour, float SunIntensity, ShadowSettings Shadows, float MaxCasterDistance)
{
    public MapCaptureLighting Freeze() => new(DirectionToLight, SunColour, SunIntensity, Shadows, MaxCasterDistance);
}
public sealed record NativeStyle(string Id, NativeRenderPolicy Render);
public sealed record NativeRenderPolicyFile(int SchemaVersion, string DefaultStyleId, NativeStyle[] Styles,
    NativeLighting Lighting, int PreviewWindowLimit, string FocusDefault)
{
    public static NativeRenderPolicyFile Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            NativePolicyJson.Validate(doc.RootElement, typeof(NativeRenderPolicyFile));
            var file = JsonSerializer.Deserialize<NativeRenderPolicyFile>(json, NativePolicyJson.Options)!;
            if (file.SchemaVersion != 1 || file.FocusDefault != "RenderStreamPlan" || file.PreviewWindowLimit < 1 ||
                string.IsNullOrWhiteSpace(file.DefaultStyleId) || file.Styles.Length == 0 ||
                file.Styles.Any(s => string.IsNullOrWhiteSpace(s.Id)) || file.Styles.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != file.Styles.Length ||
                !file.Styles.Any(s => s.Id == file.DefaultStyleId)) throw new ArgumentException("InvalidNativePolicy");
            foreach (var style in file.Styles) _ = style.Render.Freeze();
            ValidateLighting(file.Lighting); _ = file.Lighting.Freeze();
            return file;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or OverflowException or NullReferenceException)
        { throw new ArgumentException("InvalidNativePolicy", nameof(json), error); }
    }
    static void ValidateLighting(NativeLighting lighting)
    {
        var s = lighting.Shadows; var p = s.PointShadows;
        if (s.ShadowMapResolution < 256 || s.ShadowCascadeCount < 1 || s.ShadowCascadeCount > 4 || s.ShadowNearDistance <= 0 || s.ShadowMaxDistance < s.ShadowNearDistance || s.ShadowCascadeBlend < 0 || s.ShadowCascadeBlend >= .5f ||
            s.ShadowStrength < 0 || s.ShadowStrength > 1 || s.ShadowNormalOffset < 0 || s.ShadowConstantBias < 0 || s.ShadowSlopeBias < 0 ||
            s.ShadowLightHoldTexels < 0 || s.ShadowLightHoldCasterHeight < 0 || s.BlobOpacity < 0 || s.BlobOpacity > 1 ||
            s.BlobEdgeSoftness < 0 || s.BlobFadeHeight < 0 || s.BlobGroundYTolerance < 0 || s.BlobGroundMaxStep < 0 ||
            p is null || p.FaceResolution < PointShadowSettings.MinFaceResolution || p.FaceResolution > PointShadowSettings.MaxFaceResolution ||
            (p.FaceResolution & (p.FaceResolution - 1)) != 0 || p.MaxShadowedLights < 1 || p.MaxShadowedLights > PointShadowSettings.MaxLights ||
            p.MaxStaticRebuildsPerFrame < 0 || p.MaxDynamicLightsPerFrame < 0 || p.Bias < 0 || p.Bias > PointShadowSettings.MaxBias ||
            p.SlopeBias < 0 || p.SlopeBias > PointShadowSettings.MaxBias || p.LightSizeMetres < 0 || p.LightSizeMetres > PointShadowSettings.MaxLightSizeMetres ||
            p.MaxPenumbraTexels < PointShadowSettings.PenumbraTexelsFloor || p.MaxPenumbraTexels > PointShadowSettings.PenumbraTexelsCeiling)
            throw new ArgumentException("InvalidNativeLighting");
    }
}

/// <summary>Verified roots and extent metadata only. Selected decoded payloads belong to residency.</summary>
public sealed class FileNativeRenderHost : IMapNativeRenderHost
{
    readonly NativeRenderPolicyFile _file;
    readonly IReadOnlyDictionary<string, MapRenderPolicy> _styles;
    readonly MapCaptureLighting _lighting;
    public FileNativeRenderHost(string policyPath)
    {
        _file = NativeRenderPolicyFile.Read(File.ReadAllText(policyPath));
        _styles = _file.Styles.ToDictionary(s => s.Id, s => s.Render.Freeze(), StringComparer.Ordinal);
        _lighting = _file.Lighting.Freeze();
    }
    public MapNativeRenderContext Resolve(MapDocument document, string documentPath, string styleId)
    {
        if (string.IsNullOrEmpty(styleId)) styleId = _file.DefaultStyleId;
        if (!_styles.TryGetValue(styleId, out var render)) throw new ArgumentException("NativeStyleUnavailable");
        var form = MapDocumentFile.DetectForm(documentPath);
        var closure = MapAssetClosure.Load(document.NativeAssets, new MapStorageGuardedAssetSource(documentPath, form));
        using var documentSource = form == MapDocumentForm.Tiled && document.Tiles?.IsPartial == true
            ? MapDocumentSource.OpenTiled(documentPath) : MapDocumentSource.FromDocument(document);
        var captured = MapSceneSource.Capture(documentSource, closure);
        var source = captured.Source ?? throw new MapDocumentException("NativeClosureUnavailable");
        var extents = MapRenderExtentValidator.Validate(closure, source.RequiredAssetIds, new(new()));
        if (extents.Proofs is not { } proofs) throw new MapDocumentException("NativeClosureUnavailable");
        source = source.WithRenderExtents(proofs).Source ?? throw new MapDocumentException("NativeClosureUnavailable");
        return new(closure, source, proofs, render, _lighting, styleId) { PreviewWindowLimit = _file.PreviewWindowLimit };
    }
}
