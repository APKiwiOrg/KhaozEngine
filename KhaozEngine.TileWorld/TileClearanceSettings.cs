using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>One authoring rule: objects tagged with any <paramref name="SubjectTags"/> keep their meshes clear of
/// objects tagged with any <paramref name="ObstacleTags"/>, allowing <paramref name="Tolerance"/> world units of
/// overlap. Validated by <see cref="TileClearanceSettings"/>, which also takes its own copy of both tag lists.</summary>
public sealed record TileClearanceRule(string Name, IReadOnlyList<string> SubjectTags,
    IReadOnlyList<string> ObstacleTags, float Tolerance = 0f);

/// <summary>
/// A world's clearance declaration: where the object meshes live, how finely they are sampled, which tag waives
/// an object from every rule, and the rules themselves. Authoring metadata only, so it is absent from
/// <see cref="TileWorldHash.OfWorld"/> and editing it never changes a world's identity.
/// <para>Immutable. The <c>With</c> methods return a new validated instance.</para>
/// </summary>
public sealed class TileClearanceSettings
{
    public const float DefaultCellSize = 0.1f, MinCellSize = 0.02f, MaxCellSize = 1f;

    /// <param name="meshRoot">Directory of the object meshes, relative to the world directory. May be empty only
    /// while there are no rules.</param>
    /// <param name="cellSize">Sampling cell edge in world units, from <see cref="MinCellSize"/> through
    /// <see cref="MaxCellSize"/>.</param>
    /// <param name="waiverTag">Object tag that exempts an object from every rule, or null for none.</param>
    /// <param name="rules">Rules in authoring order, names unique by ordinal comparison.</param>
    /// <exception cref="ArgumentException">Any value outside the contract above, an empty tag list or tag, or a
    /// negative or non-finite tolerance.</exception>
    public TileClearanceSettings(string meshRoot, float cellSize = DefaultCellSize, string? waiverTag = null,
        IReadOnlyList<TileClearanceRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(meshRoot);
        if (!(cellSize >= MinCellSize && cellSize <= MaxCellSize))
            throw Invalid($"cellSize {cellSize} is outside {MinCellSize} through {MaxCellSize}");
        if (waiverTag is not null && string.IsNullOrWhiteSpace(waiverTag))
            throw Invalid("waiverTag cannot be empty, leave it out for no waiver");

        var copied = new List<TileClearanceRule>(rules?.Count ?? 0);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (TileClearanceRule? rule in rules ?? Array.Empty<TileClearanceRule>())
        {
            TileClearanceRule checkedRule = Validated(rule);
            if (!names.Add(checkedRule.Name)) throw Invalid($"rule '{checkedRule.Name}' is listed twice");
            copied.Add(checkedRule);
        }
        if (copied.Count > 0 && string.IsNullOrWhiteSpace(meshRoot))
            throw Invalid("meshRoot is required while rules exist");

        MeshRoot = meshRoot;
        CellSize = cellSize;
        WaiverTag = waiverTag;
        Rules = copied.AsReadOnly();
    }

    /// <summary>Directory of the object meshes, relative to the world directory.</summary>
    public string MeshRoot { get; }
    /// <summary>Sampling cell edge in world units.</summary>
    public float CellSize { get; }
    /// <summary>Object tag that exempts an object from every rule, or null.</summary>
    public string? WaiverTag { get; }
    /// <summary>Rules in authoring order.</summary>
    public IReadOnlyList<TileClearanceRule> Rules { get; }

    /// <summary>The rule with this ordinal name, or null.</summary>
    public TileClearanceRule? Rule(string name)
    {
        if (name is null) return null;
        foreach (TileClearanceRule rule in Rules)
            if (string.Equals(rule.Name, name, StringComparison.Ordinal)) return rule;
        return null;
    }

    /// <summary>Replaces the rule of the same name in place, or appends it.</summary>
    public TileClearanceSettings WithRule(TileClearanceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var next = new List<TileClearanceRule>(Rules);
        int at = next.FindIndex(r => string.Equals(r.Name, rule.Name, StringComparison.Ordinal));
        if (at >= 0) next[at] = rule;
        else next.Add(rule);
        return new TileClearanceSettings(MeshRoot, CellSize, WaiverTag, next);
    }

    /// <summary>Removes the named rule.</summary>
    /// <exception cref="ArgumentException">No rule has that name.</exception>
    public TileClearanceSettings WithoutRule(string name)
    {
        if (Rule(name) is null) throw Invalid($"no rule is named '{name}'");
        return new TileClearanceSettings(MeshRoot, CellSize, WaiverTag,
            Rules.Where(r => !string.Equals(r.Name, name, StringComparison.Ordinal)).ToList());
    }

    /// <summary>New mesh root, cell size and waiver tag, keeping the rules.</summary>
    public TileClearanceSettings With(string meshRoot, float cellSize, string? waiverTag) =>
        new(meshRoot, cellSize, waiverTag, Rules);

    // A copy with private tag lists, so a caller that keeps and edits the lists it passed in changes nothing here.
    static TileClearanceRule Validated(TileClearanceRule? rule)
    {
        if (rule is null) throw Invalid("a rule entry cannot be null");
        if (string.IsNullOrWhiteSpace(rule.Name)) throw Invalid("a rule name cannot be empty");
        if (!(rule.Tolerance >= 0f) || float.IsInfinity(rule.Tolerance))
            throw Invalid($"rule '{rule.Name}' tolerance {rule.Tolerance} must be finite and not negative");
        return rule with
        {
            SubjectTags = Tags(rule.SubjectTags, rule.Name, "subjectTags"),
            ObstacleTags = Tags(rule.ObstacleTags, rule.Name, "obstacleTags"),
        };
    }

    static ReadOnlyCollection<string> Tags(IReadOnlyList<string>? tags, string rule, string field)
    {
        if (tags is null || tags.Count == 0) throw Invalid($"rule '{rule}' {field} cannot be empty");
        string[] copy = tags.ToArray();
        foreach (string tag in copy)
            if (string.IsNullOrWhiteSpace(tag)) throw Invalid($"rule '{rule}' {field} cannot hold an empty tag");
        return Array.AsReadOnly(copy);
    }

    static ArgumentException Invalid(string detail) => new($"Invalid clearance settings: {detail}.");
}
