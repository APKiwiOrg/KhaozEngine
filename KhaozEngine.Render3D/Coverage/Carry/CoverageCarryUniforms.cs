using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageCarryCommand(int RegionIndex, Vector3 RecessionDirection, float TransitionWeight);

/// <summary>Cold-reserved palette and current command preparation. It never evaluates posed source geometry.</summary>
internal sealed class CoverageCarryUniforms
{
    internal const int MaxCommands = 4;
    readonly CoverageRefinement topology;
    readonly Matrix4x4[] composedPalette;
    readonly CoverageCarryCommand[] commands = new CoverageCarryCommand[MaxCommands];
    readonly int[] order = new int[MaxCommands];
    readonly int[] regionCommands;
    int count;
    bool prepared;

    internal CoverageCarryUniforms(CoverageRefinement topology, int boneCount)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        this.topology = topology;
        composedPalette = new Matrix4x4[boneCount];
        regionCommands = new int[topology.Map.Regions.Count];
    }

    internal ReadOnlySpan<Matrix4x4> ComposedPalette => prepared ? composedPalette : ReadOnlySpan<Matrix4x4>.Empty;
    internal ReadOnlySpan<CoverageCarryCommand> Commands => commands.AsSpan(0, count);

    internal bool TryPrepare(ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<Matrix4x4> inverseBind, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, out CoverageFailure failure)
    {
        prepared = false;
        count = 0;
        if (palette.Length != composedPalette.Length || inverseBind.Length != composedPalette.Length)
            return Refuse(topology, CoverageFailureCode.IdentityMismatch, 0, 0, 0, 0, out failure);
        if (!CoverageCarryAdmission.TryBegin(topology, commands, MaxCommands, order, regionCommands, out failure)) return false;
        bool actorValid = CoverageCarryAdmission.TryActor(model, out var actor);
        for (int slot = 0; slot < commands.Length; slot++)
        {
            CoverageCommandPose command = commands[order[slot]];
            if (!CoverageCarryAdmission.TryCommand(topology, commands, order.AsSpan(0, commands.Length), regionCommands,
                slot, actorValid, out _, out var coverage, out failure)) return false;
            if (!actor.TryInverse(out var actorInverse))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, command, out failure);
            // Retained frame composition computes in double and rounds each direction component once.
            CoverageVector direction = actorInverse.ApplyLinear(coverage.Row(2));
            Vector3 recess = new((float)direction.X, (float)direction.Y, (float)direction.Z);
            if (!CoverageRefinement.Finite(recess))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, command, out failure);
            this.commands[slot] = new(command.RegionIndex, recess, command.TransitionWeight);
        }
        CoverageReferenceSkinning.Compose(palette, inverseBind, composedPalette);
        for (int bone = 0; bone < composedPalette.Length; bone++)
            if (!CoverageAffine.TryFrom(composedPalette[bone], out _))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, 0, 0, 0, 0, out failure);
        count = commands.Length;
        prepared = true;
        return true;
    }

    internal static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, in CoverageCommandPose command,
        out CoverageFailure failure) => Refuse(topology, code, command.EquipmentInstanceId, command.Id, 0, 0, out failure);

    internal static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, ulong equipment, uint command,
        int count, int limit, out CoverageFailure failure)
    {
        failure = new(code, topology.HostIdentity, equipment, command, count, limit);
        return false;
    }
}
