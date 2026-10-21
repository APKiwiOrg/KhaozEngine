using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Literal adversarial streams, independent of the parallel partition and merge.</summary>
internal sealed class CoverageReducerCase(int count = 264)
{
    internal int Count { get; } = count;
    internal uint[] Events { get; } = new uint[264 * 6];
    internal uint[] Faces { get; } = Enumerable.Range(0, 264).SelectMany(_ => new[] { uint.MaxValue, 0u }).ToArray();
    internal uint Code, Slot = uint.MaxValue, Matching;
    internal bool Completed;

    internal void Note(int row, uint code, uint slot = 1, uint? element = null, uint local = 0, uint count = 1) =>
        new[] { code, slot, element ?? (uint)row, local, count, 0u }.CopyTo(Events, row * 6);

    internal CoverageFailure ExpectedFailure(string hostIdentity, ulong equipment, uint commandId) => Code == 0
        ? CoverageFailure.Success : new((CoverageFailureCode)Code, hostIdentity, equipment, commandId, (int)Matching, 0);

    internal static CoverageReducerCase[] For(CoverageGpuPhase phase)
    {
        bool tally = phase is CoverageGpuPhase.ReduceConform or CoverageGpuPhase.ReduceFaces or CoverageGpuPhase.ReduceFullTargets;
        bool slotFirst = phase == CoverageGpuPhase.ReduceFaces;
        var cases = new List<CoverageReducerCase> { new(0), new(1), new(63), new(64), new(65), new() };
        foreach (int row in new[] { 0, 63, 64, 65, 127, 128, 263 })
        {
            var c = new CoverageReducerCase { Code = 4, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 1u : 0u };
            c.Note(row, 4);
            cases.Add(c);
        }
        var all = new CoverageReducerCase { Code = 7, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 3u : 0u };
        int[] rows = [63, 64, 65, 127, 128, 263];
        uint[] codes = [7, 6, 5, 8, 4, 3];
        for (int i = 0; i < rows.Length; i++) all.Note(rows[i], codes[i]);
        all.Note(192, 7, count: 2);
        cases.Add(all);
        var ties = new CoverageReducerCase { Code = 4, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 2u : 0u };
        ties.Note(63, 4, element: 7); ties.Note(64, 8, element: 7); ties.Note(128, 4, element: 8);
        cases.Add(ties);
        var saturation = new CoverageReducerCase { Code = 7, Slot = tally ? (slotFirst ? 0u : 1u) : uint.MaxValue, Matching = tally ? int.MaxValue : 0u };
        saturation.Note(0, 7, element: 100, count: 2147483640);
        saturation.Note(64, 7, element: 4, count: 100);
        saturation.Note(65, 7, element: 5, count: 100);
        saturation.Note(128, 4, element: 99, count: int.MaxValue);
        saturation.Note(192, 7, 0, 101, count: int.MaxValue);
        cases.Add(saturation);
        var order = new CoverageReducerCase { Code = tally ? 7u : 6u, Slot = tally ? 1u : uint.MaxValue, Matching = tally ? 1u : 0u };
        order.Note(63, 6, element: 4, local: 1); order.Note(64, 7, element: 4, local: 0);
        cases.Add(order);
        var slot = new CoverageReducerCase { Code = slotFirst ? 3u : 4u, Slot = tally ? (slotFirst ? 0u : 1u) : uint.MaxValue, Matching = tally ? 1u : 0u };
        slot.Note(63, 4, element: 1); slot.Note(263, 3, 0, 1000);
        cases.Add(slot);
        var prior = new CoverageReducerCase { Code = 3, Slot = 1, Matching = 17, Completed = true };
        prior.Note(0, 8, 0, 0, count: int.MaxValue);
        cases.Add(prior);
        if (phase == CoverageGpuPhase.ReduceFacesOrientation) Orientation(cases);
        return cases.ToArray();
    }

    static void Orientation(List<CoverageReducerCase> cases)
    {
        // All four slots start in different lanes. Unowned triangles cannot seed a sign.
        foreach (int violation in new[] { 63, 64, 65, 127, 128, 263 })
        {
            var c = new CoverageReducerCase { Code = 8 };
            for (int t = 0; t < c.Count; t++)
            {
                c.Faces[t * 2] = (uint)(t % 4);
                c.Faces[t * 2 + 1] = t == violation ? uint.MaxValue : 1u;
            }
            cases.Add(c);
        }
        var accepted = new CoverageReducerCase();
        accepted.Faces[63 * 2] = 0; accepted.Faces[63 * 2 + 1] = uint.MaxValue;
        accepted.Faces[64 * 2] = 1; accepted.Faces[64 * 2 + 1] = 1;
        accepted.Faces[263 * 2] = 0; accepted.Faces[263 * 2 + 1] = uint.MaxValue;
        cases.Add(accepted);
        var zero = new CoverageReducerCase { Code = 8 };
        zero.Faces[64 * 2] = 3; zero.Faces[64 * 2 + 1] = 0;
        zero.Note(65, 4);
        cases.Add(zero);
        var before = new CoverageReducerCase { Code = 4 };
        before.Note(63, 4); before.Faces[64 * 2] = 2;
        cases.Add(before);
        var same = new CoverageReducerCase { Code = 3 };
        same.Note(64, 3); same.Faces[64 * 2] = 0;
        cases.Add(same);
    }
}
