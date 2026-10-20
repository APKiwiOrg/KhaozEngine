using System.Collections.Generic;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Stable cut-first scatter within each mesh, leaving all-miss streams byte-identical.</summary>
internal sealed class InteriorCutInstancePartition
{
    readonly List<uint> _cuts = new();

    internal void Clear() => _cuts.Clear();

    internal void Count(int run, bool cut)
    {
        if (run == _cuts.Count) _cuts.Add(0);
        if (cut) _cuts[run]++;
    }

    internal uint BeginRun(int run, uint start)
    {
        uint count = _cuts[run];
        _cuts[run] = start;
        return count;
    }

    internal uint TakeCut(int run) => _cuts[run]++;
}
