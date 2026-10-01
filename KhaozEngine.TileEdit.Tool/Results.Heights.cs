using System.Collections.Generic;

namespace KhaozEngine.TileEdit;

/// <summary>One archetype's collision height, as <c>archetype_set_collision_heights</c> takes it. The measure
/// verb's entries carry the same <c>id</c> and <c>height</c> names, so its good rows pass straight through.</summary>
public sealed record ArchetypeHeight(string Id, float Height);

/// <summary>One archetype as measured: its collision kind, the model top in metres above the mesh's local base
/// (null when it could not be measured), the height its catalog already records (null when none), and why it
/// could not be measured (null when it was).</summary>
public sealed record MeasuredArchetypeHeight(string Id, string CollisionKind, float? Height, float? Recorded,
    string? Error);

/// <summary>Result of <c>archetype_measure_heights</c>: the kit directory the meshes resolved under and every
/// archetype of the open catalogs, by id.</summary>
public sealed record MeasureHeightsResult(string KitRoot, IReadOnlyList<MeasuredArchetypeHeight> Archetypes);

/// <summary>A height written: the archetype, the catalog file it was written into, the height it had before
/// (null when none) and the height it has now.</summary>
public sealed record CollisionHeightChange(string Id, string File, float? Previous, float Height);

/// <summary>A height not written because the archetype already records one: what it records, what was asked
/// for, and why it was left.</summary>
public sealed record CollisionHeightSkip(string Id, float Recorded, float Requested, string Reason);

/// <summary>An archetype the verb could not measure or write, and why. An empty id is an error about the whole
/// call rather than one archetype, such as a session refresh that failed after the files were written.</summary>
public sealed record ArchetypeHeightError(string Id, string Error);

/// <summary>Result of <c>archetype_set_collision_heights</c>: what was written, what was left alone and what was
/// refused, each in the order the request listed it.</summary>
public sealed record CollisionHeightsResult(IReadOnlyList<CollisionHeightChange> Changed,
    IReadOnlyList<CollisionHeightSkip> Skipped, IReadOnlyList<ArchetypeHeightError> Errors);
