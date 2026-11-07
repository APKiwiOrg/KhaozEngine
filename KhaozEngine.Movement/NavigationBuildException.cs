using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>A navigation capture or stable read refusal. Existing invalid-operation catches remain valid.</summary>
public sealed class NavigationBuildException : InvalidOperationException
{
    /// <summary>Creates a refusal with the unchanged developer message.</summary>
    public NavigationBuildException(NavigationBuildRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>The rule that refused construction.</summary>
    public NavigationBuildRefusal Reason { get; }
    /// <summary>The geometry generation retained by the capture.</summary>
    public long? ExpectedGeneration { get; init; }
    /// <summary>The geometry generation reported by the read lease.</summary>
    public long? ActualGeneration { get; init; }
    /// <summary>The origin retained by the capture.</summary>
    public Vector3? ExpectedOrigin { get; init; }
    /// <summary>The origin reported by the refused source.</summary>
    public Vector3? ActualOrigin { get; init; }
    /// <summary>The source whose identity or live statics were refused.</summary>
    public NavigationSourceRole? SourceRole { get; init; }
    /// <summary>The query lease capability required by contact navigation.</summary>
    public Type? RequiredCapability { get; init; }
    /// <summary>The absolute X coordinate of the overfull column.</summary>
    public float? AbsoluteX { get; init; }
    /// <summary>The absolute Z coordinate of the overfull column.</summary>
    public float? AbsoluteZ { get; init; }
    /// <summary>The inclusive configured surface count limit.</summary>
    public int? MaximumCount { get; init; }
    /// <summary>The sampled surface count.</summary>
    public int? ActualCount { get; init; }
    /// <summary>The zero-based capture column X index.</summary>
    public int? ColumnX { get; init; }
    /// <summary>The zero-based capture column Z index.</summary>
    public int? ColumnZ { get; init; }
    /// <summary>The zero-based surface index within its column.</summary>
    public int? SurfaceIndex { get; init; }
    /// <summary>The refused headroom.</summary>
    public float? ActualHeadroom { get; init; }
    /// <summary>The refused absolute height.</summary>
    public float? ActualHeight { get; init; }
    /// <summary>The preceding absolute height that must be exceeded.</summary>
    public float? PreviousHeight { get; init; }
}
