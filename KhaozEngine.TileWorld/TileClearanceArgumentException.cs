using System;

namespace KhaozEngine.TileWorld;

/// <summary>A clearance declaration refusal that preserves ArgumentException catch compatibility.</summary>
public sealed class TileClearanceArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its stable reason and original parameter context.</summary>
    public TileClearanceArgumentException(TileWorldRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>Creates a refusal retaining its original cause and parameter context.</summary>
    public TileClearanceArgumentException(TileWorldRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner) => Reason = reason;

    /// <summary>The stable reason the clearance declaration was refused.</summary>
    public TileWorldRefusal Reason { get; }

    /// <summary>The supplied clearance mesh root.</summary>
    public string? MeshRoot { get; init; }

    /// <summary>The refused clearance sampling cell edge in world units.</summary>
    public float? CellSize { get; init; }

    /// <summary>The minimum permitted clearance cell size.</summary>
    public float? Minimum { get; init; }

    /// <summary>The maximum permitted clearance cell size.</summary>
    public float? Maximum { get; init; }

    /// <summary>The refused clearance waiver tag.</summary>
    public string? WaiverTag { get; init; }

    /// <summary>The refused or missing clearance rule name.</summary>
    public string? RuleName { get; init; }

    /// <summary>The number of clearance rules requiring a mesh root.</summary>
    public int? RuleCount { get; init; }

    /// <summary>The zero-based index of a null clearance rule entry.</summary>
    public int? RuleIndex { get; init; }

    /// <summary>The refused clearance field, such as subjectTags, obstacleTags or rules.</summary>
    public string? Field { get; init; }

    /// <summary>The refused clearance overlap tolerance in world units.</summary>
    public float? Tolerance { get; init; }

    /// <summary>The refused tag list count. Null when the list itself is null.</summary>
    public int? TagCount { get; init; }

    /// <summary>The zero-based index of a refused clearance tag.</summary>
    public int? TagIndex { get; init; }

    /// <summary>The refused clearance tag. Null when the tag itself is null.</summary>
    public string? Tag { get; init; }
}
