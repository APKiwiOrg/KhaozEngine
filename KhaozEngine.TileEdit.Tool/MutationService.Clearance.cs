using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>The clearance mutation verbs. Each builds the next immutable settings, which validate themselves, and
/// lands them through one <see cref="SetClearanceCommand"/>, so every call is one undo step and a refused call
/// leaves the world and its history untouched.</summary>
public sealed partial class MutationService
{
    /// <summary>Creates the clearance block, or updates its mesh root, cell size and waiver tag keeping its
    /// rules.</summary>
    public MutationResult ClearanceSet(string meshRoot, float cellSize, string? waiverTag) =>
        session.Execute(e => new SetClearanceCommand(e.Document, e.Document.Clearance is { } current
            ? current.With(meshRoot, cellSize, waiverTag)
            : new TileClearanceSettings(meshRoot, cellSize, waiverTag)));

    /// <summary>Adds a rule, or replaces the rule of the same name in place.</summary>
    /// <exception cref="TileWorldException">The world has no clearance block.</exception>
    public MutationResult ClearanceRuleSet(string name, IReadOnlyList<string> subjectTags,
        IReadOnlyList<string> obstacleTags, float tolerance) =>
        session.Execute(e => new SetClearanceCommand(e.Document,
            RequireClearance(e.Document).WithRule(new TileClearanceRule(name, subjectTags, obstacleTags, tolerance))));

    /// <summary>Removes the named rule.</summary>
    /// <exception cref="TileWorldException">The world has no clearance block.</exception>
    /// <exception cref="ArgumentException">No rule has that name.</exception>
    public MutationResult ClearanceRuleRemove(string name) =>
        session.Execute(e => new SetClearanceCommand(e.Document, RequireClearance(e.Document).WithoutRule(name)));

    static TileClearanceSettings RequireClearance(TileWorldDocument doc) =>
        doc.Clearance ?? throw new TileWorldException(
            $"world '{doc.Id}' has no clearance block, call clearance_set first");
}
