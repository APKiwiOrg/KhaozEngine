using System;
using System.Collections.Generic;
using KhaozEngine.App;

namespace KhaozEngine.Objectives;

/// <summary>
/// A game-supplied objective: a stable id, an AND-composition of <see cref="ObjectiveCondition"/>s, an opaque
/// <see cref="Metadata"/> payload the framework echoes back unchanged on completion (rewards / points / tree
/// nodes live entirely game-side), and optional localized <see cref="Name"/> / <see cref="Description"/> keys for
/// a progress log. The framework provides this model and its registration; it owns no JSON, so a game builds
/// definitions from its own data pipeline.
/// </summary>
public sealed class ObjectiveDefinition
{
    /// <summary>The game-chosen stable identity. Used as the completion key and the snapshot key; must be unique within a tracker.</summary>
    public string Id { get; }

    /// <summary>The conditions, all of which must hold simultaneously for the objective to complete. Never empty; a defensive copy of what was passed in.</summary>
    public IReadOnlyList<ObjectiveCondition> Conditions { get; }

    /// <summary>An opaque payload echoed back verbatim on <see cref="ObjectiveCompletion.Metadata"/>. The framework never inspects it (a game stashes a tier tag, reward id, tree node, etc.). Not serialized - it comes from the re-registered definition, not the snapshot.</summary>
    public object? Metadata { get; }

    /// <summary>Optional localized display name key for a progress log. A <see cref="StringId"/>, never a raw literal (presentation-free core).</summary>
    public StringId? Name { get; }

    /// <summary>Optional localized description key for a progress log.</summary>
    public StringId? Description { get; }

    /// <summary>Creates an objective definition.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null/empty, or <paramref name="conditions"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="conditions"/> is null.</exception>
    public ObjectiveDefinition(
        string id,
        IReadOnlyList<ObjectiveCondition> conditions,
        object? metadata = null,
        StringId? name = null,
        StringId? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Count == 0)
            throw new ArgumentException("An objective needs at least one condition.", nameof(conditions));

        var copy = new ObjectiveCondition[conditions.Count];
        for (int i = 0; i < conditions.Count; i++)
            copy[i] = conditions[i];

        Id = id;
        Conditions = copy;
        Metadata = metadata;
        Name = name;
        Description = description;
    }

    /// <summary>Convenience factory for the common metadata-free case: an id plus its AND-composed conditions.</summary>
    public static ObjectiveDefinition Create(string id, params ObjectiveCondition[] conditions)
        => new(id, conditions);
}
