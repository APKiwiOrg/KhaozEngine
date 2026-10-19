using System;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeHistoryGpuTests
{
    static uint[] VerifySelection(CoverageSceneFixture h, CoverageSceneFixture.NativeHistoryDraw draw)
        => VerifySelection(h, draw, out _);

    static uint[] VerifySelection(CoverageSceneFixture h, CoverageSceneFixture.NativeHistoryDraw draw, out int mismatchWords)
    {
        int mismatches = 0;
        var surface = draw.Surface;
        uint[] current = CoverageSelectedReadback.Read(h.NativeDevice, surface.Current);
        uint[] committed = CoverageSelectedReadback.Read(h.NativeDevice, surface.Previous);
        uint[] topology = CoverageSelectedReadback.Read(h.NativeDevice, surface.StaticData);
        bool seed = current[surface.CurrentBase + 15] != 0;
        uint[] previous = seed ? current : committed;
        uint previousBase = seed ? surface.CurrentBase : surface.PreviousBase;
        uint[] actual = CoverageHistorySelectionProbe.Read(h, draw);
        for (int corner = 0; corner < draw.CornerCount; corner++)
        {
            Check(current, surface.CurrentBase, corner, 0);
            Check(previous, previousBase, corner, 3);
        }
        mismatchWords = mismatches;
        Assert.Equal(0, mismatches);
        return actual;
        void Check(uint[] words, uint start, int corner, int field)
        {
            bool fallback = words[start + 14] != 0;
            uint table = topology[surface.StaticBase + (fallback ? 50u : 48u)];
            uint index = topology[surface.StaticBase + table + (uint)(corner + draw.FirstCorner)];
            uint address = start + 16 + (fallback ? 0 : topology[surface.StaticBase] * 16) + index * 16;
            for (int axis = 0; axis < 3; axis++)
                if (words[address + (uint)axis] != actual[corner * 6 + field + axis]) mismatches++;
        }
    }
}
