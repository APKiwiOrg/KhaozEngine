using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal static partial class NavBakeIdentity
{
    private const int TuningFloatCount = 26;
    private const int TuningBytes = TuningFloatCount * 4 + 1;

    // The 27 retained MoveTuning fields in declaration order. The probe runs every proof with
    // WalkSpeed 1, RunSpeed 1 and AirMomentum false, so those three never change a decision and are omitted.
    // Floats are stored as IEEE 754 bits. BackpedalAllowsRun, the only retained bool, is last and one byte.
    private static readonly TuningField[] TuningFields =
    [
        new(nameof(MoveTuning.CapsuleHalfHeight), static t => Bits(t.CapsuleHalfHeight)),
        new(nameof(MoveTuning.MaxSlopeRadians), static t => Bits(t.MaxSlopeRadians)),
        new(nameof(MoveTuning.CapsuleRadius), static t => Bits(t.CapsuleRadius)),
        new(nameof(MoveTuning.Gravity), static t => Bits(t.Gravity)),
        new(nameof(MoveTuning.JumpSpeed), static t => Bits(t.JumpSpeed)),
        new(nameof(MoveTuning.MaxFallSpeed), static t => Bits(t.MaxFallSpeed)),
        new(nameof(MoveTuning.CoyoteTime), static t => Bits(t.CoyoteTime)),
        new(nameof(MoveTuning.JumpBuffer), static t => Bits(t.JumpBuffer)),
        new(nameof(MoveTuning.AirControl), static t => Bits(t.AirControl)),
        new(nameof(MoveTuning.GroundedEpsilon), static t => Bits(t.GroundedEpsilon)),
        new(nameof(MoveTuning.StepHeight), static t => Bits(t.StepHeight)),
        new(nameof(MoveTuning.WadeStartDepthFraction), static t => Bits(t.WadeStartDepthFraction)),
        new(nameof(MoveTuning.WadeEndDepthFraction), static t => Bits(t.WadeEndDepthFraction)),
        new(nameof(MoveTuning.WadeMinSpeedScale), static t => Bits(t.WadeMinSpeedScale)),
        new(nameof(MoveTuning.SwimEnterDepthFraction), static t => Bits(t.SwimEnterDepthFraction)),
        new(nameof(MoveTuning.SwimExitDepthFraction), static t => Bits(t.SwimExitDepthFraction)),
        new(nameof(MoveTuning.SwimSpeed), static t => Bits(t.SwimSpeed)),
        new(nameof(MoveTuning.SwimSurfaceSubmersionFraction), static t => Bits(t.SwimSurfaceSubmersionFraction)),
        new(nameof(MoveTuning.SwimBuoyancyStiffness), static t => Bits(t.SwimBuoyancyStiffness)),
        new(nameof(MoveTuning.MaxStepClimbSpeed), static t => Bits(t.MaxStepClimbSpeed)),
        new(nameof(MoveTuning.AirBrakeAccel), static t => Bits(t.AirBrakeAccel)),
        new(nameof(MoveTuning.FacingTurnSpeed), static t => Bits(t.FacingTurnSpeed)),
        new(nameof(MoveTuning.TractionHysteresisRadians), static t => Bits(t.TractionHysteresisRadians)),
        new(nameof(MoveTuning.SlideFrictionRampRadians), static t => Bits(t.SlideFrictionRampRadians)),
        new(nameof(MoveTuning.StrafeSpeedScale), static t => Bits(t.StrafeSpeedScale)),
        new(nameof(MoveTuning.BackpedalSpeedScale), static t => Bits(t.BackpedalSpeedScale)),
        new(nameof(MoveTuning.BackpedalAllowsRun), static t => t.BackpedalAllowsRun ? 1u : 0u, IsBool: true),
    ];

    /// <summary>The retained <see cref="MoveTuning"/> fields in identity order, which is declaration order.</summary>
    internal static IReadOnlyList<string> TuningFieldNames { get; } = Array.ConvertAll(TuningFields, static f => f.Name);

    /// <summary>Writes the 27 retained tuning fields.</summary>
    internal static void WriteTuning(NavBakeWriter writer, in MoveTuning tuning)
    {
        foreach (TuningField field in TuningFields)
        {
            uint bits = field.Bits(tuning);
            if (field.IsBool) writer.WriteUInt8((byte)bits);
            else writer.WriteUInt32(bits);
        }
    }

    /// <summary>Reads the 27 retained tuning fields and restores the probe's <c>WalkSpeed = 1</c>,
    /// <c>RunSpeed = 1</c> and <c>AirMomentum = false</c>. False when bytes run out or the bool byte is not 0 or 1.</summary>
    internal static bool ReadTuning(ref NavBakeReader reader, out MoveTuning tuning)
    {
        tuning = default;
        Span<float> f = stackalloc float[TuningFloatCount];
        for (int i = 0; i < TuningFloatCount; i++)
            if (!reader.TryReadSingle(out f[i])) return false;
        if (!reader.TryReadUInt8(out byte backpedalAllowsRun) || backpedalAllowsRun > 1) return false;
        tuning = new MoveTuning(
            WalkSpeed: 1f,
            RunSpeed: 1f,
            CapsuleHalfHeight: f[0],
            MaxSlopeRadians: f[1],
            CapsuleRadius: f[2],
            Gravity: f[3],
            JumpSpeed: f[4],
            MaxFallSpeed: f[5],
            CoyoteTime: f[6],
            JumpBuffer: f[7],
            AirControl: f[8],
            GroundedEpsilon: f[9],
            StepHeight: f[10],
            WadeStartDepthFraction: f[11],
            WadeEndDepthFraction: f[12],
            WadeMinSpeedScale: f[13],
            SwimEnterDepthFraction: f[14],
            SwimExitDepthFraction: f[15],
            SwimSpeed: f[16],
            SwimSurfaceSubmersionFraction: f[17],
            SwimBuoyancyStiffness: f[18],
            MaxStepClimbSpeed: f[19],
            AirMomentum: false,
            AirBrakeAccel: f[20],
            FacingTurnSpeed: f[21],
            TractionHysteresisRadians: f[22],
            SlideFrictionRampRadians: f[23],
            StrafeSpeedScale: f[24],
            BackpedalSpeedScale: f[25],
            BackpedalAllowsRun: backpedalAllowsRun == 1);
        return true;
    }

    private static string? FirstTuningDifference(in MoveTuning bake, in MoveTuning want)
    {
        foreach (TuningField field in TuningFields)
        {
            uint stored = field.Bits(bake), expected = field.Bits(want);
            if (stored == expected) continue;
            return field.IsBool
                ? $"field {field.Name}: bake {stored == 1u}, expected {expected == 1u}"
                : $"field {field.Name}: bake {FormatFloat(stored)}, expected {FormatFloat(expected)}";
        }
        return null;
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private readonly record struct TuningField(string Name, Func<MoveTuning, uint> Bits, bool IsBool = false);
}
