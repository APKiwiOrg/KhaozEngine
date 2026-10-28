using System;
using System.Linq;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="TemporalVisibilityFrozenDLaterChanges"/>: every production change after D is carried whole by each
/// program that holds it, the D rewrite leaves none of its production text behind, and a change carried in part is
/// refused. <c>TemporalVisibilityFrozenDTests</c> holds the result to the programs D shipped. Device-free.
/// </summary>
public sealed class TemporalVisibilityFrozenDLaterChangesTests
{
    static readonly string[] EntryPrograms =
    [
        ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
        ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
        ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
    ];

    [Fact]
    public void Every_entry_program_carries_each_later_change_whole()
    {
        foreach (string program in EntryPrograms)
            foreach (TemporalVisibilityFrozenDLaterChanges.Change change in TemporalVisibilityFrozenDLaterChanges.Changes)
            {
                foreach (TemporalVisibilityFrozenDLaterChanges.Undo undo in change.Core)
                    Assert.True(Count(program, undo.Production) == 1, $"{change.Name}.{undo.Name}");
                foreach (TemporalVisibilityFrozenDLaterChanges.Undo undo in change.Optional)
                    Assert.True(Count(program, undo.Production) == 1, $"{change.Name}.{undo.Name}");
            }
    }

    [Fact]
    public void The_D_rewrite_leaves_no_later_production_text()
    {
        string[] programs = [.. EntryPrograms, ShaderSources.TemporalDebugFrag, ShaderSources.TemporalProbeFrag];
        foreach (string program in programs)
        {
            string d = TemporalVisibilityFrozenD.ToD(program);
            foreach (TemporalVisibilityFrozenDLaterChanges.Change change in TemporalVisibilityFrozenDLaterChanges.Changes)
                foreach (TemporalVisibilityFrozenDLaterChanges.Undo undo in change.Core.Concat(change.Optional))
                    Assert.True(Count(d, undo.Production) == 0, $"{change.Name}.{undo.Name} survived the D rewrite");
        }
    }

    [Fact]
    public void A_later_change_carried_in_part_is_refused()
    {
        string lostStep = ShaderSources.TemporalAccumulateFrag.Replace(
            TemporalVisibilityFrozenDLaterChanges.RecordStep.Production, "", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => TemporalVisibilityFrozenD.ToD(lostStep));
        string storeAlone = ShaderSources.TemporalDepthStoreFrag + "\n// "
            + TemporalVisibilityFrozenDLaterChanges.RecordStore.Production;
        Assert.Throws<InvalidOperationException>(() => TemporalVisibilityFrozenD.ToD(storeAlone));
        string twoStores = ShaderSources.TemporalResolveFrag + "\n// "
            + TemporalVisibilityFrozenDLaterChanges.RecordStore.Production;
        Assert.Throws<InvalidOperationException>(() => TemporalVisibilityFrozenD.ToD(twoStores));
    }

    static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
