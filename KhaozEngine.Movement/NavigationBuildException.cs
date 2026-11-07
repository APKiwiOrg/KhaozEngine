using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>A navigation capture, proof or stable read refusal. Existing invalid-operation catches remain valid.</summary>
public sealed class NavigationBuildException : InvalidOperationException
{
    /// <summary>Creates a refusal with the unchanged developer message.</summary>
    public NavigationBuildException(NavigationBuildRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>The rule that refused construction.</summary>
    public NavigationBuildRefusal Reason { get; }
    /// <summary>The generation retained by the capture or snapshot.</summary>
    public long? ExpectedGeneration { get; init; }
    /// <summary>The generation compared by the refused guard.</summary>
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
    /// <summary>The zero-based proof cell X index.</summary>
    public int? CellX { get; init; }
    /// <summary>The zero-based proof cell Z index.</summary>
    public int? CellZ { get; init; }
    /// <summary>The availability reported by the refused acquisition or anchor query.</summary>
    public MovementAvailability? Availability { get; init; }
    /// <summary>The frame retained by the proof or snapshot.</summary>
    public MovementFrameDescriptor? ExpectedFrame { get; init; }
    /// <summary>The frame compared by the refused guard.</summary>
    public MovementFrameDescriptor? ActualFrame { get; init; }
    /// <summary>The identity retained from the first proof lease.</summary>
    public MovementQueryLeaseId? ExpectedLease { get; init; }
    /// <summary>The identity of the refused proof lease.</summary>
    public MovementQueryLeaseId? ActualLease { get; init; }
    /// <summary>The required envelope minimum in the physics frame.</summary>
    public Vector3? Minimum { get; init; }
    /// <summary>The required envelope maximum in the physics frame.</summary>
    public Vector3? Maximum { get; init; }
    /// <summary>The foot radius used to construct the unbounded support footprint.</summary>
    public float? FootRadius { get; init; }
    /// <summary>The reach used to construct the unbounded support footprint.</summary>
    public float? SupportReach { get; init; }
    /// <summary>The slope in radians used to construct the unbounded support footprint.</summary>
    public float? SupportSlope { get; init; }
    /// <summary>The water extent retained by the snapshot.</summary>
    public MovementWaterExtent? ExpectedExtent { get; init; }
    /// <summary>The water extent compared by the refused guard.</summary>
    public MovementWaterExtent? ActualExtent { get; init; }
    /// <summary>The water semantic identity retained by the snapshot.</summary>
    public string? ExpectedIdentity { get; init; }
    /// <summary>The water semantic identity compared by the refused guard.</summary>
    public string? ActualIdentity { get; init; }
}
