using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

// Shared by every cell movement system on one server. Registration ends before the first authoritative tick, so
// cell workers only read this dictionary while simulation is running.
sealed class TileActorTraversalRegistry
{
    readonly Dictionary<TileActorTraversalProfile, TileActorTraversalEntry> entries = new();
    bool closed;

    internal TileActorTraversalRegistry(TileMoveSimulator defaultSimulator)
    {
        ArgumentNullException.ThrowIfNull(defaultSimulator);
        entries.Add(TileActorTraversalProfile.Default,
            new TileActorTraversalEntry(defaultSimulator.Map, defaultSimulator));
    }

    internal void Register(TileActorTraversalProfile profile, TileCollisionMap map,
        TileMoveSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(simulator);
        if (closed)
            throw new InvalidOperationException(
                "Actor traversal profiles must be registered before the first authoritative tick.");
        if (profile == TileActorTraversalProfile.Default)
            throw new ArgumentException("The default actor traversal profile is already registered.", nameof(profile));
        if (profile == TileActorTraversalProfile.Unresolved)
            throw new ArgumentException("The reserved unresolved actor traversal profile cannot be registered.",
                nameof(profile));
        if (!entries.TryAdd(profile, new TileActorTraversalEntry(map, simulator)))
            throw new ArgumentException($"Actor traversal profile {profile.Value} is already registered.",
                nameof(profile));
    }

    internal bool TryGet(TileActorTraversalProfile profile, out TileActorTraversalEntry entry) =>
        entries.TryGetValue(profile, out entry);

    internal void Close() => closed = true;
}

readonly record struct TileActorTraversalEntry(TileCollisionMap Map, TileMoveSimulator Simulator);
