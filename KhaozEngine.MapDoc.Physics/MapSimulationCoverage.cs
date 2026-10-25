using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Cell-owned minimum geometry and annotations. Its leases never depend on observer windows.</summary>
public sealed class MapSimulationCoverage
{
    readonly MapSceneSource _source;
    readonly MapAssetClosure _assets;
    readonly MapWorldBuildOptions _options;
    public MapSimulationCoverage(MapSceneSource source, MapAssetClosure assets, MapWorldBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        _source = source; _assets = assets; _options = options;
    }

    public MapSimulationLease Acquire(IReadOnlyList<MapSceneScope> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        const MapSceneChannels minimum = MapSceneChannels.Terrain | MapSceneChannels.Placements | MapSceneChannels.Water |
            MapSceneChannels.Annotations | MapSceneChannels.Rooms;
        using var ownership = new Acquisition(MapRequiredDomainCoverage.Acquire(_source, domains, minimum));
        var coverage = ownership.Coverage;
        if (!coverage.Witness.CompleteForScope)
            return new(coverage.Status, coverage.Witness, Array.Empty<MapScopedWorld>(), null)
            { Capacity = coverage.Capacity, Findings = coverage.Findings };
        var witness = coverage.Witness;
        var worlds = new List<MapScopedWorld>();
        foreach (var scene in coverage.Scenes)
        {
            var result = MapScopedWorldBuilder.Build(scene, _assets, _options);
            witness = MapRequiredDomainCoverage.Extend(witness, result.Witness);
            if (result.World is null)
                return new(result.Status, witness with { CompleteForScope = false }, Array.Empty<MapScopedWorld>(), null)
                { Capacity = result.Capacity, Findings = result.Findings };
            worlds.Add(result.World);
        }
        ownership.Transferred = true;
        return new(MapSceneStatus.CompleteForScope, witness, worlds.AsReadOnly(), coverage);
    }

    sealed class Acquisition(MapSceneCoverageLease coverage) : IDisposable
    {
        internal MapSceneCoverageLease Coverage => coverage;
        internal bool Transferred;
        public void Dispose() { if (!Transferred) coverage.Dispose(); }
    }
}

/// <summary>Held by cell minimums and destination admission through publication, then explicitly released.</summary>
public sealed class MapSimulationLease : IDisposable
{
    MapSceneCoverageLease? _coverage;
    public MapSceneStatus Status { get; }
    public MapSceneWitness Witness { get; }
    public MapSceneCapacity? Capacity { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MapScopedWorld> Worlds { get; private set; }
    internal MapSimulationLease(MapSceneStatus status, MapSceneWitness witness, IReadOnlyList<MapScopedWorld> worlds,
        MapSceneCoverageLease? coverage)
    { Status = status; Witness = witness; Worlds = worlds; _coverage = coverage; }
    public void Dispose()
    {
        Worlds = Array.Empty<MapScopedWorld>();
        _coverage?.Dispose(); _coverage = null;
    }
}
