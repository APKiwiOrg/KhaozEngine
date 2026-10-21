using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Frozen caller style and explicit conservative model extents. Neither selects animated placements.</summary>
public sealed class NativeFoliagePolicy
{
    public MapFrozenFoliageOptions Options { get; }
    public IReadOnlyDictionary<string, MapFoliageModelExtent> Extents { get; }
    public IReadOnlyDictionary<string, GroundCoverStableModelPolicy> Models { get; }
    public string Digest { get; }
    public NativeFoliagePolicy(MapFrozenFoliageOptions options, IReadOnlyDictionary<string, MapFoliageModelExtent> extents)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(extents);
        Validate(options);
        var frozen = new Dictionary<string, MapFoliageModelExtent>(StringComparer.Ordinal);
        foreach (var (id, extent) in extents)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (extent is null || id != extent.AssetId || !Positive(extent.SourceUnitsToMetres) ||
                !Nonnegative(extent.MotionPaddingMetres) || !Finite(extent.BoundsInSourceUnits.Min) || !Finite(extent.BoundsInSourceUnits.Max) ||
                extent.BoundsInSourceUnits.Min.X > extent.BoundsInSourceUnits.Max.X || extent.BoundsInSourceUnits.Min.Y > extent.BoundsInSourceUnits.Max.Y ||
                extent.BoundsInSourceUnits.Min.Z > extent.BoundsInSourceUnits.Max.Z)
                throw new ArgumentException("Native foliage requires valid explicitly padded model extents.", nameof(extents));
            frozen.Add(id, extent);
        }
        Options = options; Extents = new ReadOnlyDictionary<string, MapFoliageModelExtent>(frozen);
        var shared = new GroundCoverRenderOptions
        { CastsShadows = options.CastsShadows, MotionProfiles = options.MotionProfiles, ModelCastsShadows = options.ModelCastsShadows };
        Models = new ReadOnlyDictionary<string, GroundCoverStableModelPolicy>(frozen.Keys.ToDictionary(id => id,
            id => GroundCoverModelPolicy.Resolve(shared, id), StringComparer.Ordinal));
        Digest = MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/native-foliage-policy/1");
            foreach (string id in frozen.Keys.Order(StringComparer.Ordinal))
            {
                var extent = frozen[id]; var model = Models[id];
                w.WriteStringValue(id);
                var min = extent.BoundsInSourceUnits.Min; var max = extent.BoundsInSourceUnits.Max;
                foreach (float value in new[] { min.X, min.Y, min.Z, max.X, max.Y, max.Z, extent.SourceUnitsToMetres,
                    extent.MotionPaddingMetres, model.Motion.BendScale, model.Motion.MaxLean, model.Motion.WindScale, model.Motion.Shiver })
                    w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                w.WriteBooleanValue(model.CastShadows);
            }
            w.WriteEndArray();
        });
    }
    static void Validate(MapFrozenFoliageOptions options)
    {
        if (!Nonnegative(options.DrawRadius) || options.DensityRadius is { } radius && !Nonnegative(radius) ||
            !Nonnegative(options.FadeBandWidth) || !Nonnegative(options.InstanceFadeBandWidth) ||
            !Unit(options.QualityDensity) || !Unit(options.DistantDensity) || !Nonnegative(options.WindFadeBladePixels) ||
            !float.IsFinite(options.WindDirection.X) || !float.IsFinite(options.WindDirection.Y) || !Unit(options.WindStrength) ||
            !Nonnegative(options.WindSpeed) || !Nonnegative(options.WindSpatialFrequency) ||
            options.FadeMode is not GroundCoverFadeMode.Dissolve and not GroundCoverFadeMode.HeightScale)
            throw new ArgumentException("Native foliage frame defaults must be finite and in their allowed ranges.", nameof(options));
        if (!options.UseGpuBatches || options.FadeMode != GroundCoverFadeMode.HeightScale)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Native animated foliage requires retained height-scaled batches.");
    }
    static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
    static bool Unit(float value) => Nonnegative(value) && value <= 1;
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
