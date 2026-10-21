using System;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Serial traversal oracle, independent of workgroup stripes and capped tree merges.</summary>
internal readonly record struct CoverageReducerReference(uint Code, uint Slot = uint.MaxValue, uint Matching = 0)
{
    internal CoverageFailure Failure(string hostIdentity, ulong equipment, uint commandId) => Code == 0
        ? CoverageFailure.Success : new((CoverageFailureCode)Code, hostIdentity, equipment, commandId, (int)Matching, 0);

    internal static CoverageReducerReference Evaluate(CoverageGpuPhase phase, CoverageReducerCase item)
    {
        if (item.Completed) return new(3, 1, 17);
        if (phase is CoverageGpuPhase.ReduceConform or CoverageGpuPhase.ReduceFaces or CoverageGpuPhase.ReduceFullTargets)
            return Tally(phase, item);
        int?[] firstSigns = new int?[4];
        for (int row = 0; row < item.NoteCount; row++)
        {
            uint code = item.Events[row * 6];
            if (code != 0) return new(code);
            if (phase != CoverageGpuPhase.ReduceFacesOrientation) continue;
            int slot = unchecked((int)item.Faces[row * 2]);
            if (slot < 0) continue;
            slot = Math.Min(slot, 3);
            int sign = unchecked((int)item.Faces[row * 2 + 1]);
            firstSigns[slot] ??= sign;
            if (sign == 0 || sign != firstSigns[slot]) return new(8);
        }
        return new(0);
    }

    static CoverageReducerReference Tally(CoverageGpuPhase phase, CoverageReducerCase item)
    {
        // Reference traversal is sorted slot, element, local note, then stable physical row.
        int[] rows = Enumerable.Range(0, item.NoteCount).Where(row => item.Events[row * 6] != 0)
            .OrderBy(row => phase == CoverageGpuPhase.ReduceFaces ? item.Events[row * 6 + 1] : 0)
            .ThenBy(row => item.Events[row * 6 + 2]).ThenBy(row => item.Events[row * 6 + 3]).ToArray();
        if (rows.Length == 0) return new(0);
        uint code = item.Events[rows[0] * 6], slot = item.Events[rows[0] * 6 + 1];
        ulong matching = 0;
        foreach (int row in rows)
            if (item.Events[row * 6] == code && item.Events[row * 6 + 1] == slot)
                matching += item.Events[row * 6 + 4];
        return new(code, slot, (uint)Math.Min(matching, int.MaxValue));
    }
}
