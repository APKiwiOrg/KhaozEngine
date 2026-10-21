using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public readonly partial struct PropLayer
{
    /// <summary>Frozen anchor-local instances for a native placement layer. Null for the released layer kinds.</summary>
    public IReadOnlyList<NativePropInstance>? NativePlacements => _native?.Instances;
    /// <summary>Every declared native tier and part, frozen independently of the caller's collections.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<NativePropLod>>? NativeLods => _native?.Lods;
    /// <summary>Caller thresholds for native far tiers. Empty retains base-only rendering.</summary>
    public IReadOnlyList<float> LodDistances => _native?.Distances ?? Array.Empty<float>();

    /// <summary>A native render-only placement layer with exact anchors and N declared tiers. Draw through
    /// PropRenderer.DrawNative or the native typed recorder. The legacy analytic chunk sink accepts released
    /// placement, scatter and companion layers only.</summary>
    public static PropLayer PlacementLayer(IReadOnlyList<NativePropInstance> instances,
        IReadOnlyDictionary<string, IReadOnlyList<NativePropLod>> lods, float drawRadius,
        IReadOnlyList<float> lodDistances, float lodCrossfadeWidth = 0, bool castsShadows = true)
    {
        if (!float.IsFinite(drawRadius) || drawRadius <= 0 || !float.IsFinite(lodCrossfadeWidth) || lodCrossfadeWidth < 0)
            throw new ArgumentException("Native radii and crossfade widths must be finite with a positive draw radius.");
        var native = new NativeInventory(instances, lods, lodDistances);
        return new(null, null, -1, EmptyMeshes, null, drawRadius, 0, null, null, 0,
            registerColliders: false, castsShadows: castsShadows, lodCrossfadeWidth: lodCrossfadeWidth,
            native: native);
    }

    sealed class NativeInventory
    {
        internal IReadOnlyList<NativePropInstance> Instances { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<NativePropLod>> Lods { get; }
        internal IReadOnlyList<float> Distances { get; }
        internal NativeInventory(IReadOnlyList<NativePropInstance> instances,
            IReadOnlyDictionary<string, IReadOnlyList<NativePropLod>> lods, IReadOnlyList<float> distances)
        {
            ArgumentNullException.ThrowIfNull(instances); ArgumentNullException.ThrowIfNull(lods); ArgumentNullException.ThrowIfNull(distances);
            var rows = instances.ToArray(); var thresholds = distances.ToArray();
            if (rows.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.AssetId)) ||
                rows.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
                throw new ArgumentException("Native layers require distinct placement identities.", nameof(instances));
            float previous = 0;
            foreach (float distance in thresholds)
            {
                if (!float.IsFinite(distance) || distance <= previous) throw new ArgumentException("Native thresholds must be positive and increasing.", nameof(distances));
                previous = distance;
            }
            var tiers = new Dictionary<string, IReadOnlyList<NativePropLod>>(StringComparer.Ordinal);
            foreach (var pair in lods)
            {
                ArgumentNullException.ThrowIfNull(pair.Value);
                var values = pair.Value.ToArray();
                if (values.Length == 0 || values.Where((tier, index) => tier is null || tier.Index != index).Any())
                    throw new ArgumentException("Native tiers must start at zero and retain every declared tier.", nameof(lods));
                tiers.Add(pair.Key, Array.AsReadOnly(values));
            }
            foreach (var row in rows)
                if (!tiers.TryGetValue(row.AssetId, out var values) || thresholds.Length > 0 && thresholds.Length != values.Count - 1)
                    throw new ArgumentException("Every native placement requires all tiers and their caller thresholds.", nameof(lods));
            Instances = Array.AsReadOnly(rows); Distances = Array.AsReadOnly(thresholds);
            Lods = new ReadOnlyDictionary<string, IReadOnlyList<NativePropLod>>(tiers);
        }
    }
}
