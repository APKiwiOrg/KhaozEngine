using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Maps terrain-feature discriminators to their DTO types and runtime builders. Games extend the
/// document format by registering custom feature types here (engine changes not required). Not thread-safe
/// during registration; register everything at startup, then share.</summary>
public sealed class MapDocRegistry
{
    readonly Dictionary<string, (Type DocType, Func<MapFeature, ITerrainFeature> Build)> _features =
        new(StringComparer.Ordinal);

    /// <summary>A registry pre-loaded with the built-in feature types: lake, flatten, ridge, rim.</summary>
    public static MapDocRegistry CreateDefault()
    {
        var r = new MapDocRegistry();
        r.RegisterFeature("lake", typeof(LakeFeatureDoc), f => ((LakeFeatureDoc)f).Build());
        r.RegisterFeature("flatten", typeof(FlattenFeatureDoc), f => ((FlattenFeatureDoc)f).Build());
        r.RegisterFeature("ridge", typeof(RidgeFeatureDoc), f => ((RidgeFeatureDoc)f).Build());
        r.RegisterFeature("rim", typeof(RimFeatureDoc), f => ((RimFeatureDoc)f).Build());
        return r;
    }

    /// <summary>Registers a feature DTO type under a discriminator. The DTO's <see cref="MapFeature.Type"/>
    /// must return the same discriminator.</summary>
    /// <exception cref="ArgumentException">The discriminator is already registered, or the type is not a
    /// <see cref="MapFeature"/>.</exception>
    public void RegisterFeature(string type, Type docType, Func<MapFeature, ITerrainFeature> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(docType);
        ArgumentNullException.ThrowIfNull(build);
        if (!typeof(MapFeature).IsAssignableFrom(docType))
            throw new ArgumentException($"{docType.Name} does not derive from MapFeature.", nameof(docType));
        if (_features.ContainsKey(type))
            throw new ArgumentException($"Feature type '{type}' is already registered.", nameof(type));
        _features.Add(type, (docType, build));
    }

    /// <summary>Resolves a discriminator to its DTO type for deserialization.</summary>
    public bool TryGetFeatureDocType(string type, out Type docType)
    {
        if (_features.TryGetValue(type, out var entry)) { docType = entry.DocType; return true; }
        docType = typeof(MapFeature);
        return false;
    }

    /// <summary>Builds the runtime <see cref="ITerrainFeature"/> for a feature DTO.</summary>
    /// <exception cref="MapDocumentException">The feature's discriminator is not registered.</exception>
    public ITerrainFeature BuildFeature(MapFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (!_features.TryGetValue(feature.Type, out var entry))
            throw new MapDocumentException($"Unknown terrain feature type '{feature.Type}'. Register it on the MapDocRegistry.");
        return entry.Build(feature);
    }
}
