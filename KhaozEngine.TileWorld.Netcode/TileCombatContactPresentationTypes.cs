using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Opt-in combat body presentation settings, in tiles and seconds.</summary>
public sealed record TileCombatContactPresentationSettings
{
    public float ResponseSeconds { get; init; } = 0.02f;
    public float ReleaseSeconds { get; init; } = 0.15f;
    public float MaxAdjustmentSpeedTilesPerSecond { get; init; } = 4f;
    public float MaxGoalOffsetTiles { get; init; } = 3f;
    public float ContactToleranceTiles { get; init; } = 0.04f;
    public byte TerminalHoldTicks { get; init; } = 2;
    public ushort MaxParticipants { get; init; } = 256;

    internal void Validate()
    {
        ValidatePositive(ResponseSeconds, nameof(ResponseSeconds));
        ValidatePositive(ReleaseSeconds, nameof(ReleaseSeconds));
        ValidatePositive(MaxAdjustmentSpeedTilesPerSecond, nameof(MaxAdjustmentSpeedTilesPerSecond));
        ValidatePositive(MaxGoalOffsetTiles, nameof(MaxGoalOffsetTiles));
        ValidatePositive(ContactToleranceTiles, nameof(ContactToleranceTiles));
        if (ContactToleranceTiles >= MaxGoalOffsetTiles)
            throw new ArgumentOutOfRangeException(nameof(ContactToleranceTiles), ContactToleranceTiles,
                "Contact tolerance must be less than the goal offset limit.");
        if (TerminalHoldTicks == 0)
            throw new ArgumentOutOfRangeException(nameof(TerminalHoldTicks), TerminalHoldTicks,
                "Terminal hold must be at least one tick.");
        if (MaxParticipants < 2)
            throw new ArgumentOutOfRangeException(nameof(MaxParticipants), MaxParticipants,
                "Participant capacity must be at least two.");
    }

    static void ValidatePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(name, value, "Must be finite and positive.");
    }
}

/// <summary>A displayed combat body. Positions, corrections and velocities use world metres.</summary>
public readonly record struct TileCombatBodyPresentation(
    TilePose Pose, Vector3 Velocity, Vector3 BaseVelocity, Vector3 Correction,
    long SourceServerTick, bool Discontinuity, TileCombatContactLimits Limits);

/// <summary>An immutable contact measurement for one accepted combat result, in world metres.</summary>
public readonly record struct TileCombatContactImpact(
    ulong AttackId, uint Revision, long AttackerNetId, long TargetNetId,
    long ImpactTick, long GeometryServerTick, Vector3 DesiredRelativePosition,
    float PlanarErrorMetres, bool WithinTolerance, TileCombatContactLimits Limits);

/// <summary>Named constraints on combat body presentation and contact measurements.</summary>
[Flags]
public enum TileCombatContactLimits
{
    None = 0,
    MissingGeometry = 1,
    LatePreparation = 2,
    LateOutcome = 4,
    ChangedGeometry = 8,
    ConflictingLayout = 16,
    GoalDistance = 32,
    ParticipantCapacity = 64,
    Collision = 128,
    ReleasePath = 256,
    TargetUnavailable = 512,
    PresentationCut = 1024
}
