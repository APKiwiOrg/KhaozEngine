using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of skeleton arrays, a required name or compose buffers.</summary>
public sealed class SkeletonArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public SkeletonArgumentException(SkeletonRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public SkeletonRefusal Reason { get; }

    /// <summary>The number of parent indices in mismatched node arrays.</summary>
    public int? ParentCount { get; init; }

    /// <summary>The number of rest poses in mismatched node arrays.</summary>
    public int? RestCount { get; init; }

    /// <summary>The number of logical indices in mismatched node arrays.</summary>
    public int? LogicalIndexCount { get; init; }

    /// <summary>The number of names in a mismatched name table.</summary>
    public int? NameCount { get; init; }

    /// <summary>The number of nodes the name table must cover.</summary>
    public int? NodeCount { get; init; }

    /// <summary>The required name absent from the skeleton.</summary>
    public string? RequestedName { get; init; }

    /// <summary>The refused compose buffer length.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The required compose buffer length.</summary>
    public int? ExpectedCount { get; init; }

    IReadOnlyList<string>? _knownNames;

    /// <summary>The non-empty skeleton names for a missing-name refusal. Setting this property copies the
    /// source into an owned read-only snapshot that cannot be changed through collection interfaces.</summary>
    public IReadOnlyList<string>? KnownNames
    {
        get => _knownNames;
        init => _knownNames = value is null ? null : ImmutableArray.Create(value.ToArray());
    }
}
