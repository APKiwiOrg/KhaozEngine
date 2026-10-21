using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Literal adversarial streams, independent of the parallel partition and merge.</summary>
internal sealed class CoverageReducerCase(CoverageGpuPhase phase, int? topologyCount = null, string name = "legacy")
{
    internal static int NotesPerElement(CoverageGpuPhase phase) => phase switch
    {
        CoverageGpuPhase.ReduceFaces => 3,
        CoverageGpuPhase.ReduceFullTargets => 2,
        _ => 1
    };

    internal int TopologyCount { get; } = topologyCount ?? 264 / NotesPerElement(phase);
    internal int NoteCount => TopologyCount * NotesPerElement(phase);
    internal string Name { get; } = name;
    static int Capacity(CoverageGpuPhase phase, int? topologyCount) =>
        Math.Max(264, (topologyCount ?? 264 / NotesPerElement(phase)) * NotesPerElement(phase) + 1);

    internal uint[] Events { get; } = new uint[Capacity(phase, topologyCount) * 6];
    internal uint[] Faces { get; } = Enumerable.Range(0, Capacity(phase, topologyCount))
        .SelectMany(_ => new[] { uint.MaxValue, 0u }).ToArray();
    internal uint Code, Slot = uint.MaxValue, Matching;
    internal bool Completed;

    internal void Note(int row, uint code, uint slot = 1, uint? element = null, uint local = 0, uint count = 1) =>
        new[] { code, slot, element ?? (uint)row, local, count, 0u }.CopyTo(Events, row * 6);

    internal static CoverageReducerCase[] For(CoverageGpuPhase phase)
    {
        bool tally = phase is CoverageGpuPhase.ReduceConform or CoverageGpuPhase.ReduceFaces or CoverageGpuPhase.ReduceFullTargets;
        bool slotFirst = phase == CoverageGpuPhase.ReduceFaces;
        var cases = new List<CoverageReducerCase>();
        int factor = NotesPerElement(phase);
        int[] boundaries = [0, 1, 63, 64, 65, 127, 128];
        // T and G are topology counts. Their physical note extents are exactly 3T and 2G.
        // Also straddle physical lane boundaries where an exact multiple is not representable.
        foreach (int count in boundaries.Concat(boundaries.SelectMany(n => new[] { n / factor, (n + factor - 1) / factor })).Distinct().Order())
        {
            var clean = new CoverageReducerCase(phase, count, $"clean-{count}");
            cases.Add(clean);
            if (clean.NoteCount > 0)
            {
                var tail = new CoverageReducerCase(phase, count, $"tail-{count}")
                { Code = 4, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 1u : 0u };
                tail.Note(tail.NoteCount - 1, 4);
                cases.Add(tail);
            }
            var excluded = new CoverageReducerCase(phase, count, $"excluded-{count}");
            excluded.Note(excluded.NoteCount, 4);
            cases.Add(excluded);
            if (phase == CoverageGpuPhase.ReduceFacesOrientation)
            {
                if (count > 0)
                {
                    var tailSign = new CoverageReducerCase(phase, count, $"tail-sign-{count}") { Code = 8 };
                    tailSign.Faces[(count - 1) * 2] = 2;
                    cases.Add(tailSign);
                }
                var excludedSign = new CoverageReducerCase(phase, count, $"excluded-sign-{count}");
                excludedSign.Faces[count * 2] = 3;
                cases.Add(excludedSign);
            }
        }
        cases.Add(new(phase));
        foreach (int row in new[] { 0, 63, 64, 65, 127, 128, 263 })
        {
            var c = new CoverageReducerCase(phase) { Code = 4, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 1u : 0u };
            c.Note(row, 4);
            cases.Add(c);
        }
        var all = new CoverageReducerCase(phase) { Code = 7, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 3u : 0u };
        int[] rows = [63, 64, 65, 127, 128, 263];
        uint[] codes = [7, 6, 5, 8, 4, 3];
        for (int i = 0; i < rows.Length; i++) all.Note(rows[i], codes[i]);
        all.Note(192, 7, count: 2);
        cases.Add(all);
        var ties = new CoverageReducerCase(phase) { Code = 4, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 2u : 0u };
        ties.Note(63, 4, element: 7); ties.Note(64, 8, element: 7); ties.Note(128, 4, element: 8);
        cases.Add(ties);
        var saturation = new CoverageReducerCase(phase) { Code = 7, Slot = tally ? (slotFirst ? 0u : 1u) : uint.MaxValue, Matching = tally ? int.MaxValue : 0u };
        saturation.Note(0, 7, element: 100, count: 2147483640);
        saturation.Note(64, 7, element: 4, count: 100);
        saturation.Note(65, 7, element: 5, count: 100);
        saturation.Note(128, 4, element: 99, count: int.MaxValue);
        saturation.Note(192, 7, 0, 101, count: int.MaxValue);
        cases.Add(saturation);
        var order = new CoverageReducerCase(phase) { Code = tally ? 7u : 6u, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 1u : 0u };
        order.Note(63, 6, element: 4, local: 1); order.Note(64, 7, element: 4, local: 0);
        cases.Add(order);
        var slot = new CoverageReducerCase(phase) { Code = slotFirst ? 3u : 4u, Slot = tally ? (slotFirst ? 0u : 1u) : uint.MaxValue, Matching = tally ? 1u : 0u };
        slot.Note(63, 4, element: 1); slot.Note(263, 3, 0, 1000);
        cases.Add(slot);
        var prior = new CoverageReducerCase(phase) { Code = 3, Slot = 1, Matching = 17, Completed = true };
        prior.Note(0, 8, 0, 0, count: int.MaxValue);
        cases.Add(prior);
        if (phase == CoverageGpuPhase.ReduceFaces)
        {
            var wrap = new CoverageReducerCase(phase, name: "winning-unsigned-wrap")
            { Code = 7, Slot = 0, Matching = int.MaxValue };
            wrap.Note(0, 7, 0, count: 2147483640);
            wrap.Note(64, 7, 0, count: 100);
            wrap.Note(65, 7, 0, count: int.MaxValue);
            cases.Add(wrap);
        }
        if (phase == CoverageGpuPhase.ReduceFacesOrientation) Orientation(cases);
        return cases.ToArray();
    }

    static void Orientation(List<CoverageReducerCase> cases)
    {
        const CoverageGpuPhase phase = CoverageGpuPhase.ReduceFacesOrientation;
        // All four slots start in different lanes. Unowned triangles cannot seed a sign.
        foreach (int violation in new[] { 63, 64, 65, 127, 128, 263 })
        {
            var c = new CoverageReducerCase(phase) { Code = 8 };
            for (int t = 0; t < c.NoteCount; t++)
            {
                c.Faces[t * 2] = (uint)(t % 4);
                c.Faces[t * 2 + 1] = t == violation ? uint.MaxValue : 1u;
            }
            cases.Add(c);
        }
        var accepted = new CoverageReducerCase(phase);
        accepted.Faces[63 * 2] = 0; accepted.Faces[63 * 2 + 1] = uint.MaxValue;
        accepted.Faces[64 * 2] = 1; accepted.Faces[64 * 2 + 1] = 1;
        accepted.Faces[263 * 2] = 0; accepted.Faces[263 * 2 + 1] = uint.MaxValue;
        cases.Add(accepted);
        var opposite = new CoverageReducerCase(phase, name: "opposite-slots-2-3");
        opposite.Faces[0] = 2; opposite.Faces[1] = 1;
        opposite.Faces[2] = 3; opposite.Faces[3] = uint.MaxValue;
        opposite.Faces[64 * 2] = 2; opposite.Faces[64 * 2 + 1] = 1;
        opposite.Faces[65 * 2] = 3; opposite.Faces[65 * 2 + 1] = uint.MaxValue;
        cases.Add(opposite);
        foreach (int slot in new[] { 0, 1, 2, 3 })
        {
            var first = new CoverageReducerCase(phase, name: $"first-sign-slot-{slot}") { Code = 4 };
            first.Faces[0] = (uint)slot; first.Faces[1] = 1;
            first.Note(63, 4);
            first.Faces[64 * 2] = (uint)slot; first.Faces[64 * 2 + 1] = uint.MaxValue;
            cases.Add(first);
        }
        var zero = new CoverageReducerCase(phase) { Code = 8 };
        zero.Faces[64 * 2] = 3; zero.Faces[64 * 2 + 1] = 0;
        zero.Note(65, 4);
        cases.Add(zero);
        var before = new CoverageReducerCase(phase) { Code = 4 };
        before.Note(63, 4); before.Faces[64 * 2] = 2;
        cases.Add(before);
        var same = new CoverageReducerCase(phase) { Code = 3 };
        same.Note(64, 3); same.Faces[64 * 2] = 0;
        cases.Add(same);
    }
}
