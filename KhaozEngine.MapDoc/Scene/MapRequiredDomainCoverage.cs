using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Atomically acquires caller-owned required domains without any observer or presentation lifetime.</summary>
public static class MapRequiredDomainCoverage
{
    public static MapSceneCoverageLease Acquire(MapSceneSource source, IReadOnlyList<MapSceneScope> domains)
        => Acquire(source, domains, 0);

    internal static MapSceneCoverageLease Acquire(MapSceneSource source, IReadOnlyList<MapSceneScope> domains, MapSceneChannels requiredChannels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(domains);
        var admission = new MapSceneAdmission(source.Data.Limits);
        using var activation = admission.Enter();
        using var preparation = admission.BeginPreparation();
        var witnesses = new List<MapSceneWitness>();
        string identity = "";
        try
        {
            if (domains.Count == 0) throw new ArgumentException("Required coverage needs at least one domain.");
            admission.ReserveBytes(checked(domains.Count * 128L));
            foreach (var domain in domains) ArgumentNullException.ThrowIfNull(domain);
            var requested = domains.Select(d => d with { Channels = d.Channels | requiredChannels }).ToArray();
            identity = MapCanonical.HashHex(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/required-domains/1");
                foreach (string digest in requested.Select(d => d.Digest).Distinct().Order(StringComparer.Ordinal))
                    w.WriteStringValue(digest);
                w.WriteEndArray();
            });
            var scenes = new List<MapScopedScene>();
            foreach (var domain in requested.DistinctBy(d => d.Digest))
            {
                var result = MapSceneAcquisition.Run(source, domain, admission);
                witnesses.Add(result.Witness);
                if (result.Scene is null || !result.Witness.CompleteForScope)
                    return new(result.Status, Combine(source, identity, witnesses, false), Array.Empty<MapScopedScene>())
                    { Capacity = result.Capacity, Findings = result.Findings };
                scenes.Add(result.Scene);
            }
            return new(MapSceneStatus.CompleteForScope, Combine(source, identity, witnesses, true), scenes.AsReadOnly())
            { PreparationOwnership = preparation.Complete(checked(128L * requested.Length + scenes.Sum(s => s.InventoryBytes))) };
        }
        catch (Exception error) when (MapSceneSource.Classify(error) is not null)
        {
            return new(Enum.Parse<MapSceneStatus>(MapSceneSource.Classify(error)!.Value.ToString()),
                Combine(source, identity, witnesses, false), Array.Empty<MapScopedScene>())
            { Capacity = MapSceneCapacity.From(error), Findings = [error.Message] };
        }
    }

    internal static MapSceneWitness Extend(MapSceneWitness scope, MapSceneWitness dependency)
    {
        if (scope.SourceGeneration != dependency.SourceGeneration || scope.ClosureHash != dependency.ClosureHash)
            throw new MapSceneSourceException(MapSceneStatus.Stale, "Coverage dependencies belong to another source generation.");
        IReadOnlyList<MapSceneResourceKey> Merge(IReadOnlyList<MapSceneResourceKey> a, IReadOnlyList<MapSceneResourceKey> b) =>
            Array.AsReadOnly(a.Concat(b).Distinct().OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray());
        return scope with
        {
            Required = Merge(scope.Required, dependency.Required),
            Present = Merge(scope.Present, dependency.Present),
            KnownEmpty = Merge(scope.KnownEmpty, dependency.KnownEmpty),
            Unavailable = Merge(scope.Unavailable, dependency.Unavailable),
            CompleteForScope = scope.CompleteForScope && dependency.CompleteForScope,
            WholeWorldComplete = false,
        };
    }

    static MapSceneWitness Combine(MapSceneSource source, string identity, IReadOnlyList<MapSceneWitness> witnesses, bool complete)
    {
        IReadOnlyList<MapSceneResourceKey> Keys(Func<MapSceneWitness, IReadOnlyList<MapSceneResourceKey>> get) =>
            Array.AsReadOnly(witnesses.SelectMany(get).Distinct().OrderBy(k => k.Kind, StringComparer.Ordinal)
                .ThenBy(k => k.Id, StringComparer.Ordinal).ToArray());
        return new(source.ClosureHash, source.SourceGeneration, identity, Keys(w => w.Required), Keys(w => w.Present),
            complete ? Keys(w => w.KnownEmpty) : Array.Empty<MapSceneResourceKey>(), Keys(w => w.Unavailable), complete, false)
        { RenderExtentsDigest = source.RenderExtentsDigest };
    }
}

/// <summary>Owns neutral snapshots until the last consumer of this lease explicitly releases it.</summary>
public sealed class MapSceneCoverageLease : IDisposable
{
    internal MapPreparationReservation? PreparationOwnership { get; init; }
    public MapSceneStatus Status { get; }
    public MapSceneWitness Witness { get; }
    public MapSceneCapacity? Capacity { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MapScopedScene> Scenes { get; private set; }
    internal MapSceneCoverageLease(MapSceneStatus status, MapSceneWitness witness, IReadOnlyList<MapScopedScene> scenes)
    { Status = status; Witness = witness; Scenes = scenes; }
    public void Dispose() { PreparationOwnership?.Dispose(); Scenes = Array.Empty<MapScopedScene>(); }
}
