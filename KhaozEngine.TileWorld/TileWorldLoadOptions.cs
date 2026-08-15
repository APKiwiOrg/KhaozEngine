using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace KhaozEngine.TileWorld;

/// <summary>Load options: format-version migrations over the manifest object, each stepping N to N+1 and
/// forming a contiguous run up to <see cref="TileWorldFile.CurrentFormatVersion"/> (checked at load). None are
/// built in yet: the format is at version 1.</summary>
public sealed class TileWorldLoadOptions
{
    internal readonly SortedDictionary<int, Func<JsonObject, JsonObject>> Migrations = new();

    /// <summary>Registers the step that takes a manifest from <paramref name="fromVersion"/> to the next
    /// version. One step per source version.</summary>
    public void RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (Migrations.ContainsKey(fromVersion))
            throw new ArgumentException($"A migration from formatVersion {fromVersion} is already registered.", nameof(fromVersion));
        Migrations.Add(fromVersion, step);
    }
}
