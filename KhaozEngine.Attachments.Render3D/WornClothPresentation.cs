using System;
using System.Numerics;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Requests a prewarmed slot for a named piece. A bare set uses worn index zero.</summary>
public readonly record struct WornClothSelection(WornClothSlot Slot, int WornIndex, uint PartId);

/// <summary>The current display tier of the selected piece.</summary>
public enum WornClothTier
{
    Simulated,
    Held,
    Cloth,
}

/// <summary>Why the selected piece could not change tier or take a cloth step.</summary>
public enum WornClothFallback
{
    None,
    Hidden,
    Unprepared,
    IncompatibleProfile,
    SlotOccupied,
    UnsupportedClock,
    UnsupportedTransform,
    SupportCapacity,
    SolveRefused,
    TransferRefused,
}

/// <summary>The selected piece's display state and bounded work for the last accepted wearer pose.</summary>
public readonly record struct WornClothStatus(WornClothTier Tier, WornClothFallback Fallback,
    uint? PartId, int Substeps, ClothStepResult Solve, ClothStepResult Projection)
{
    public uint? RequestedPartId { get; init; }
    public bool IsBlending { get; init; }
    public ClothContactWork SolveWork { get; init; }
    public long MaterialFactorProducts { get; init; }
    public int LowerSegments { get; init; }
    public int RigidSupports { get; init; }
    public int Hulls { get; init; }
}

/// <summary>The borrowed CPU surface used for one displayed pose.</summary>
/// <remarks>Read the nodes before the next step, reset, rebind or release. Positions are world-aligned metres
/// relative to <see cref="WorldOrigin"/>. Use <see cref="RelativeModel"/> to reconstruct in model space
/// without first adding a large world translation. The fitted surface identifies the visible material views.</remarks>
public readonly ref struct WornClothPresentation
{
    internal WornClothPresentation(WornMotion motion, ulong revision, ulong historyRevision, int wornIndex, BoundClothSurface surface,
        ReadOnlySpan<Vector3> nodes, Vector3 worldOrigin, in Matrix4x4 model, ReadOnlySpan<Matrix4x4> garmentPalette)
    {
        Motion = motion;
        DisplayRevision = revision;
        HistoryRevision = historyRevision;
        WornIndex = wornIndex;
        Surface = surface;
        Nodes = nodes;
        WorldOrigin = worldOrigin;
        Model = model;
        GarmentPalette = garmentPalette;
        var relativeModel = model;
        relativeModel.Translation -= worldOrigin;
        RelativeModel = relativeModel;
    }

    public WornMotion Motion { get; }
    public ulong DisplayRevision { get; }
    public ulong HistoryRevision { get; }
    public bool IsCurrentPose => DisplayRevision == Motion.PoseRevision;
    public int WornIndex { get; }
    public uint PartId => Surface.PartId;
    public BoundClothSurface Surface { get; }
    public ReadOnlySpan<Vector3> Nodes { get; }
    public Vector3 WorldOrigin { get; }
    public Matrix4x4 Model { get; }
    public Matrix4x4 RelativeModel { get; }

    /// <summary>Garment-space pose matrices for the fixed vertices at the accepted display pose.</summary>
    public ReadOnlySpan<Matrix4x4> GarmentPalette { get; }
}
