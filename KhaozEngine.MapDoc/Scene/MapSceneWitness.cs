using System.Collections.Generic;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapDoc.Scene;

public sealed record MapSceneResourceKey(string Kind, string Id);
public sealed record MapSceneResourceMetadata(MapSceneResourceKey Key, MapTileCoord StorageOwner, MapBox3 Bounds, string Digest);
public enum MapSceneStatus { CompleteForScope, WholeWorldComplete, Incomplete, Missing, Corrupt, Stale, CapacityExceeded, NotRepresentable, Invalid }
public enum MapSceneSourceStatus { Ready, Incomplete, Missing, Corrupt, Stale, CapacityExceeded, NotRepresentable, Invalid }
public sealed record MapSceneSourceResult(MapSceneSourceStatus Status, MapSceneSource? Source, MapSceneWork Work)
{
    public IReadOnlyList<string> Findings { get; init; } = System.Array.Empty<string>();
}
public sealed record MapScopedSceneResult(MapSceneStatus Status, MapScopedScene? Scene, MapSceneWitness Witness, MapSceneWork Work)
{
    public IReadOnlyList<string> Findings { get; init; } = System.Array.Empty<string>();
}

public sealed record MapSceneWitness(string ClosureHash, string SourceGeneration, string ScopeDigest,
    IReadOnlyList<MapSceneResourceKey> Required, IReadOnlyList<MapSceneResourceKey> Present,
    IReadOnlyList<MapSceneResourceKey> KnownEmpty, IReadOnlyList<MapSceneResourceKey> Unavailable,
    bool CompleteForScope, bool WholeWorldComplete)
{
    public string? RenderExtentsDigest { get; init; }
}
