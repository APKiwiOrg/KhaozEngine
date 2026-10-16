using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Validates captured draw evidence and reserves the combined position error within each authored margin.
/// Scene ownership and completeness of the captured pass schedule are enforced by the capturing scene.</summary>
internal static class CoverageRenderError
{
    internal static bool TryBound(CoverageRefinement topology, ReadOnlySpan<ModelVertex> finalVertices, in Matrix4x4 actorModel,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, in CoverageRenderContext context,
        out double worldErrorBound, out CoverageFailure failure)
    {
        worldErrorBound = 0;
        if (!ReferenceEquals(context.Topology, topology) || context.Passes.IsEmpty)
            return Refuse(CoverageFailureCode.IdentityMismatch, 0, 0, out failure);
        CoverageRenderStamp stamp = context.Stamp;
        if (stamp.SceneIdentity == 0 || stamp.FrameGeneration == 0 || stamp.ViewGeneration == 0 || stamp.PreparationId == 0 || stamp.ActorKey.IsNone)
            return Refuse(CoverageFailureCode.StaleFrame, 0, 0, out failure);
        if (!CoverageRefinement.Finite(context.RenderOrigin))
            return Refuse(CoverageFailureCode.UnsupportedTransform, 0, 0, out failure);
        Span<double> equipmentErrors = stackalloc double[CoverageScratch.MaxCommands];
        Span<bool> seen = stackalloc bool[CoverageScratch.MaxCommands];
        equipmentErrors.Clear(); seen.Clear();
        bool hostSeen = false;
        double hostError = 0;
        foreach (ref readonly CoverageRenderPass pass in context.Passes)
        {
            int slot = -1;
            if (pass.CommandId == 0)
            {
                if (pass.EquipmentInstanceId != 0) return Refuse(CoverageFailureCode.IdentityMismatch, pass.EquipmentInstanceId, 0, out failure);
                hostSeen = true;
            }
            else
            {
                for (int s = 0; s < scratch.ActiveCommands; s++)
                    if (commands[scratch.Frames[s].Command].Id == pass.CommandId) { slot = s; break; }
                if (slot < 0 || pass.EquipmentInstanceId == 0 || commands[scratch.Frames[slot].Command].EquipmentInstanceId != pass.EquipmentInstanceId)
                    return Refuse(CoverageFailureCode.IdentityMismatch, pass.EquipmentInstanceId, pass.CommandId, out failure);
                seen[slot] = true;
            }
            Matrix4x4 absolute = slot < 0 ? actorModel : commands[scratch.Frames[slot].Command].EquipmentWorld;
            if (!CoverageAffine.TryFrom(pass.Model, out _) || !CoverageRefinement.Finite(pass.LocalMin) || !CoverageRefinement.Finite(pass.LocalMax)
                || pass.LocalMin.X > pass.LocalMax.X || pass.LocalMin.Y > pass.LocalMax.Y || pass.LocalMin.Z > pass.LocalMax.Z
                || !double.IsFinite(pass.PositionPerturbationBound) || pass.PositionPerturbationBound < 0)
                return Refuse(CoverageFailureCode.UnsupportedTransform, pass.EquipmentInstanceId, pass.CommandId, out failure);
            Matrix4x4 expected = absolute;
            expected.M41 -= context.RenderOrigin.X; expected.M42 -= context.RenderOrigin.Y; expected.M43 -= context.RenderOrigin.Z;
            if (pass.Model != expected)
                return Refuse(CoverageFailureCode.StaleFrame, pass.EquipmentInstanceId, pass.CommandId, out failure);
            if (slot < 0)
            {
                foreach (ref readonly ModelVertex vertex in finalVertices)
                    if (!Contains(pass, vertex.Position)) return Refuse(CoverageFailureCode.StaleFrame, 0, 0, out failure);
            }
            else
            {
                foreach (Vector3 position in commands[scratch.Frames[slot].Command].Support.Source.Positions)
                    if (!Contains(pass, position)) return Refuse(CoverageFailureCode.StaleFrame, pass.EquipmentInstanceId, pass.CommandId, out failure);
            }
            if (!CoverageRenderArithmetic.TryBound(pass, absolute, context.RenderOrigin, out double error))
                return Refuse(CoverageFailureCode.UnsupportedTransform, pass.EquipmentInstanceId, pass.CommandId, out failure);
            if (slot < 0) hostError = Math.Max(hostError, error);
            else equipmentErrors[slot] = Math.Max(equipmentErrors[slot], error);
        }
        if (!hostSeen) return Refuse(CoverageFailureCode.IdentityMismatch, 0, 0, out failure);
        for (int slot = 0; slot < scratch.ActiveCommands; slot++)
        {
            ref CoverageCommandFrame frame = ref scratch.Frames[slot];
            ref readonly CoverageCommandPose command = ref commands[frame.Command];
            if (!seen[slot]) return Refuse(CoverageFailureCode.IdentityMismatch, command.EquipmentInstanceId, command.Id, out failure);
            double pair = CoverageRenderArithmetic.Sum(hostError, equipmentErrors[slot]);
            double consumed = CoverageRenderArithmetic.Sum(pair, frame.ConstructionErrorBound);
            if (!double.IsFinite(consumed) || consumed > command.Clearance * CoverageFrames.MarginFraction)
                return Refuse(CoverageFailureCode.UnsupportedTransform, command.EquipmentInstanceId, command.Id, out failure);
            // Every commanded host region is checked against this equipment with this same pair allowance.
            frame.RenderErrorBound = pair;
            worldErrorBound = Math.Max(worldErrorBound, pair);
        }
        if (scratch.ActiveCommands == 0) worldErrorBound = hostError;
        failure = default;
        return true;

        bool Refuse(CoverageFailureCode code, ulong equipment, uint id, out CoverageFailure refused) =>
            CoverageFrames.Refuse(topology, code, equipment, id, 0, 0, out refused);
    }

    static bool Contains(in CoverageRenderPass pass, Vector3 p) => CoverageRefinement.Finite(p)
        && p.X >= pass.LocalMin.X && p.Y >= pass.LocalMin.Y && p.Z >= pass.LocalMin.Z
        && p.X <= pass.LocalMax.X && p.Y <= pass.LocalMax.Y && p.Z <= pass.LocalMax.Z;
}
