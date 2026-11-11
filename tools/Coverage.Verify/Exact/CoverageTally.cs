using System;

using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Records the first refusal in deterministic vertex or triangle order and counts its repeats for the same
/// command, so a failure carries a complete count without depending on command submission order.</summary>
internal struct CoverageTally
{
    CoverageFailureCode code;
    int slot, count;

    internal readonly bool Failed => code != CoverageFailureCode.None;

    internal void Note(CoverageFailureCode failure, int commandSlot)
    {
        if (code == CoverageFailureCode.None) { code = failure; slot = commandSlot; count = 1; }
        else if (code == failure && slot == commandSlot && count < int.MaxValue) count++;
    }

    internal readonly bool Succeeded(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch,
        out CoverageFailure failure)
    {
        Span<int> slotCommands = stackalloc int[CoverageScratch.MaxCommands];
        for (int i = 0; i < scratch.ActiveCommands; i++) slotCommands[i] = scratch.Frames[i].Command;
        return Succeeded(topology, commands, slotCommands, out failure);
    }

    internal readonly bool Succeeded(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        ReadOnlySpan<int> slotCommands, out CoverageFailure failure)
    {
        if (code == CoverageFailureCode.None) { failure = default; return true; }
        if (slot < 0) return CoverageFrames.Refuse(topology, code, 0, 0, count, 0, out failure);
        CoverageCommandPose command = commands[slotCommands[slot]];
        return CoverageFrames.Refuse(topology, code, command.EquipmentInstanceId, command.Id, count, 0, out failure);
    }
}
