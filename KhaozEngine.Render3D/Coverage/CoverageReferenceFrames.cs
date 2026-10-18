using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageReferenceFrame(int Command, Matrix4x4 HostToCoverage, Matrix4x4 HostToActualSheet,
    Matrix4x4 ActorLinear, Vector3 Recess, float ZScale, float CapMargin, float ClearanceCoverage,
    float ActualClearanceCoverage, float CorrectionRate);

/// <summary>CPU-composed affine coefficients, rounded once, followed by the pinned scalar float frame graph.</summary>
internal static class CoverageReferenceFrames
{
    internal static bool TryPrepare(CoverageRefinement topology, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        if (!ReferenceEquals(topology, scratch.ReferenceTopology.Topology))
            return CoverageFrames.Refuse(topology, CoverageFailureCode.IdentityMismatch, 0, 0, 0, 0, out failure);
        if (!CoverageFrameAdmission.TryBegin(topology, commands, scratch.CommandCapacity, scratch.Order, scratch.RegionCommands, out failure)) return false;
        Span<int> order = scratch.Order.AsSpan(0, commands.Length);
        scratch.UniformArithmetic.Reset();
        scratch.ActiveCommands = commands.Length;
        if (commands.Length == 0) return true;
        bool actorValid = CoverageFrameAdmission.TryActor(model, out var actor);
        for (int slot = 0; slot < order.Length; slot++)
        {
            CoverageCommandPose command = commands[order[slot]];
            if (!CoverageFrameAdmission.TryCommand(topology, commands, order, scratch.RegionCommands, slot, actorValid,
                out var equipment, out var coverage, out failure)) return false;
            if (!Derive(scratch.UniformArithmetic, actor, equipment, coverage, command, order[slot], out scratch.Frames[slot]))
                return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedTransform, command, out failure);
        }
        return true;
    }

    static bool Derive(CoverageReferenceArithmetic f, in CoverageAffine actor, in CoverageAffine equipment, in CoverageAffine coverage, in CoverageCommandPose command,
        int commandIndex, out CoverageReferenceFrame frame)
    {
        frame = default;
        if (!actor.TryInverse(out var actorInverse) || !coverage.TryInverse(out var coverageInverse) || !equipment.TryInverse(out var equipmentInverse)
            || !CoverageAffine.TryFrom(command.Support.SourceToCoverage, out var projection)) return false;
        Matrix4x4 h = Round(CoverageAffine.Multiply(actor, coverageInverse));
        CoverageAffine actualInverse = CoverageAffine.Multiply(equipmentInverse, projection);
        Matrix4x4 j = Round(CoverageAffine.Multiply(CoverageAffine.Multiply(actor, equipmentInverse), projection));
        Matrix4x4 inverseCoverage = Round(coverageInverse), inverseActual = Round(actualInverse), linear = Round(actor.WithTranslation(default));
        CoverageVector direction = actorInverse.ApplyLinear(coverage.Row(2));
        Vector3 recess = new((float)direction.X, (float)direction.Y, (float)direction.Z);
        Vector3 z = new(command.CoverageWorld.M31, command.CoverageWorld.M32, command.CoverageWorld.M33);
        float zScale = MathF.Sqrt(CoverageReferenceMath.Dot3(f, z, z));
        Vector3 column = new(inverseCoverage.M13, inverseCoverage.M23, inverseCoverage.M33);
        float capMargin = command.Clearance * MathF.Sqrt(CoverageReferenceMath.Dot3(f, column, column));
        float slope = command.Support.ReferenceData.MaxSlope;
        float delta = command.Clearance * .25f;
        float reserved = command.Clearance + delta;
        float steepness = MathF.Sqrt(1 + (slope * slope));
        float clearance = (reserved * steepness) * SpectralUpper(f, inverseCoverage);
        float actualClearance = (reserved * steepness) * SpectralUpper(f, inverseActual);
        Vector3 d = CoverageReferenceMath.Linear(f, recess, j);
        float correctionRate = d.Z - (slope * MathF.Sqrt((d.X * d.X) + (d.Y * d.Y)));
        if (!Finite(h) || !Finite(j) || !Finite(linear) || !CoverageRefinement.Finite(recess) || !float.IsFinite(zScale)
            || !float.IsFinite(capMargin) || !float.IsFinite(clearance) || !float.IsFinite(actualClearance)
            || !float.IsFinite(correctionRate) || !(correctionRate > 0)) return false;
        frame = new(commandIndex, h, j, linear, recess, zScale, capMargin, clearance, actualClearance, correctionRate);
        return true;
    }

    static float SpectralUpper(CoverageReferenceArithmetic f, in Matrix4x4 m)
    {
        Vector3 r1 = new(m.M11, m.M12, m.M13), r2 = new(m.M21, m.M22, m.M23), r3 = new(m.M31, m.M32, m.M33);
        float g11 = CoverageReferenceMath.Dot3(f, r1, r1), g22 = CoverageReferenceMath.Dot3(f, r2, r2), g33 = CoverageReferenceMath.Dot3(f, r3, r3);
        float g12 = MathF.Abs(CoverageReferenceMath.Dot3(f, r1, r2)), g13 = MathF.Abs(CoverageReferenceMath.Dot3(f, r1, r3)),
            g23 = MathF.Abs(CoverageReferenceMath.Dot3(f, r2, r3));
        return MathF.Sqrt(MathF.Max((g11 + g12) + g13, MathF.Max((g12 + g22) + g23, (g13 + g23) + g33)));
    }

    static Matrix4x4 Round(in CoverageAffine affine)
    {
        CoverageVector a = affine.Row(0), b = affine.Row(1), c = affine.Row(2), t = affine.Translation;
        return new((float)a.X, (float)a.Y, (float)a.Z, 0, (float)b.X, (float)b.Y, (float)b.Z, 0,
            (float)c.X, (float)c.Y, (float)c.Z, 0, (float)t.X, (float)t.Y, (float)t.Z, 1);
    }

    static bool Finite(in Matrix4x4 m) => CoverageAffine.TryFrom(m, out _);
}
