using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageDispatchObserverTests
{
    [Fact]
    public void Observer_brackets_each_dispatch_in_order_and_is_unset_by_default()
    {
        using var fixture = new CoverageSceneFixture();
        var batch = fixture.Scene.CoverageGpuBatchForTests!;
        var property = typeof(CoverageGpuBatch).GetProperty("DispatchObserverForTests", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        Assert.Null(property.GetValue(batch));
        var observed = new List<(CoverageGpuPhase Phase, bool Entering, int Count)>();
        Action<IGpuCommandList, CoverageGpuPhase, bool> observer = (commands, phase, entering) =>
        {
            Assert.Same(fixture.Record, commands);
            observed.Add((phase, entering, fixture.Record.DrawCount));
        };
        property.SetValue(batch, observer);
        fixture.Begin();
        var frame = fixture.Stage();
        fixture.Scene.DrawSkinned(fixture.HostDraw(frame), fixture.Palette);
        fixture.SubmitEquipment();
        fixture.Render();
        var phases = Enum.GetValues<CoverageGpuPhase>();
        Assert.Equal(phases.Length * 2, observed.Count);
        for (int i = 0; i < phases.Length; i++)
        {
            Assert.Equal((phases[i], true), (observed[2 * i].Phase, observed[2 * i].Entering));
            Assert.Equal((phases[i], false), (observed[2 * i + 1].Phase, observed[2 * i + 1].Entering));
            Assert.Equal(observed[2 * i].Count + 1, observed[2 * i + 1].Count);
        }
        property.SetValue(batch, null);
        fixture.Begin();
        frame = fixture.Stage();
        fixture.Scene.DrawSkinned(fixture.HostDraw(frame), fixture.Palette);
        fixture.SubmitEquipment();
        fixture.Render();
        Assert.Equal(phases.Length * 2, observed.Count);
    }
}
