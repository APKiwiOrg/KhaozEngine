using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepuPhysics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal static class SupportProposalAdmission
{
    // Only identity has a tier-independent zero/one arithmetic proof. Other quaternion bits,
    // including signed zeros, retain the full scalar/SIMD correspondence predicate.
    static readonly bool s_qualifiedRuntime = Environment.Version.Major == 10 && Vector.IsHardwareAccelerated &&
        Vector<float>.Count is 4 or 8 or 16 &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
    readonly record struct Key(int X, int Y, int Z, int W, ulong Controls);
    struct Slot
    {
        internal Key Key;
        internal bool Populated, Result;
    }

    [ThreadStatic] static Slot t_slot;
    internal delegate bool ControlRead(out ulong controls);
    [ThreadStatic] internal static ControlRead? ControlReaderForTests;
    [ThreadStatic] internal static bool BypassForTests;

    internal static void ResetForTests() => t_slot = default;
    internal static int PayloadBytesForTests => Unsafe.SizeOf<Slot>();

    internal static bool Admits(in Pose pose)
    {
        // Translation feeds neither resolution nor operational coefficient agreement. It must
        // still pass the original finite magnitude gate before any cached Boolean is consumed.
        if (!InstalledPoseOperator.Within(pose.Position, RepresentedGeometryTransforms.MaximumPositionMagnitude))
            return false;
        Quaternion q = pose.Orientation;
        var key = new Key(Bits(q.X), Bits(q.Y), Bits(q.Z), Bits(q.W), 0);
        if (SupportReferenceEvaluation.Active || BypassForTests || !s_qualifiedRuntime ||
            key.X != 0 || key.Y != 0 || key.Z != 0 || key.W != 0x3f800000 ||
            !TryReadControls(out ulong controls))
        {
            SupportProposalAdmissionCapture.Record(null);
            return Original(pose);
        }
        key = key with { Controls = controls };
        if (t_slot.Populated && t_slot.Key == key)
        {
            SupportProposalAdmissionCapture.Record(true);
            return t_slot.Result;
        }
        SupportProposalAdmissionCapture.Record(false);
        bool result = Original(pose);
        t_slot = new() { Key = key, Populated = true, Result = result };
        return result;
    }

    static bool TryReadControls(out ulong controls) => ControlReaderForTests is { } reader
        ? reader(out controls) : PhysicsFloatingPointControls.TryRead(out controls);
    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    static bool Original(in Pose pose) =>
        InstalledPoseOperator.TryCreateSupport(new RigidPose(pose.Position, pose.Orientation), out _);
}
