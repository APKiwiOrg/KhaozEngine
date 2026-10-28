using System;
using System.Globalization;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The yard and resolve-cost scenes' common path export, coverage and production-lock comparison.</summary>
internal static class TemporalVisibilityPathShareReadback
{
    internal static (int Width, int Height) DisplaySize()
    {
        using GpuDeviceContext probe = GpuDeviceContext.CreateHeadless();
        return TemporalResolveCostPerfGpuTests.IsSoftwareDevice(probe.GpuDevice.Capabilities.DeviceName)
            ? (480, 270) : (2560, 1440);
    }

    // Install before the first resolve. The scene owns and disposes the renderer.
    internal static TemporalResolveRenderer UseExportingResolve(TemporalFixture fx, Func<string, string>? rewrite = null)
    {
        FieldInfo field = typeof(Scene3D).GetField("_temporalResolve", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scene3D no longer holds its resolve renderer in _temporalResolve");
        Assert.Null(field.GetValue(fx.Scene));
        var renderer = new TemporalResolveRenderer(fx.Device, rewrite ?? TemporalVisibilityProductionPaths.Rewrite);
        field.SetValue(fx.Scene, renderer);
        return renderer;
    }

    internal static string Share(long part, long whole) => whole == 0 ? "n/a"
        : (100.0 * part / whole).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    internal static void ReportAndAssert(TemporalFixture exporting, TemporalFixture production, int frame,
        ITestOutputHelper output, Action<float[]>? inspectAndDecode = null)
    {
        TemporalHistory history = exporting.Scene.TemporalHistory;
        float[] exported = TemporalTextureIo.Read(exporting.Device, history.Color(history.WriteIndex));
        float[] exportedState = TemporalTextureIo.Read(exporting.Device, history.Confidence(history.WriteIndex));
        TemporalHistory productionHistory = production.Scene.TemporalHistory;
        float[] productionState = TemporalTextureIo.Read(production.Device,
            productionHistory.Confidence(productionHistory.WriteIndex));
        long pixels = (long)exporting.DisplayWidth * exporting.DisplayHeight;
        Assert.Equal(pixels * 4, exported.LongLength);
        inspectAndDecode?.Invoke(exported);
        var paths = new TemporalVisibilityProductionPaths();
        paths.Add(exported);
        long lockDiffers = 0;
        for (int p = 0; p < pixels; p++)
            if (BitConverter.SingleToInt32Bits(exportedState[p * 4 + 1])
                != BitConverter.SingleToInt32Bits(productionState[p * 4 + 1]))
                lockDiffers++;

        string S(long n) => Share(n, pixels);
        output.WriteLine($"  frame {frame}, {paths.Pixels} display pixels by visibility path:");
        output.WriteLine($"    not reaching the call site {S(paths.NotReached)}");
        output.WriteLine($"    gated off by a zero lock (R1) {S(paths.GatedByLock)}");
        output.WriteLine($"    gated off otherwise (no history, background, first frame) {S(paths.GatedOtherwise)}");
        output.WriteLine($"    the check runs {S(paths.FastPath + paths.SlowPath)}: fast {S(paths.FastPath)} "
            + $"(range skip {S(paths.RangeSkips)}, change computed {S(paths.FastComputed)}, nothing accepted "
            + $"{S(paths.FastWithoutChange)}), slow {S(paths.SlowPath)} (at an edge {S(paths.SlowEdge)}, "
            + $"on a base deviation {S(paths.SlowDeviation)}, at the border {S(paths.SlowBorder)}, at a binade "
            + $"{S(paths.SlowBinade)}, change computed {S(paths.ChangeComputed - paths.FastComputed)})");
        output.WriteLine($"    released {S(paths.Released)} (on the fast path {S(paths.FastReleased)})");
        output.WriteLine($"    exact path counts: {paths}");
        output.WriteLine($"  stored locks apart from a production scene rendered beside it: {lockDiffers} of {pixels}");

        Assert.True(lockDiffers == 0, $"{lockDiffers} of {pixels} stored locks differ from the production scene's");
        Assert.Equal(pixels, paths.Pixels);
        Assert.Equal(pixels, paths.NotReached + paths.GatedByLock + paths.GatedOtherwise + paths.FastPath + paths.SlowPath);
        Assert.Equal(paths.SlowPath, paths.SlowEdge + paths.SlowDeviation + paths.SlowBorder + paths.SlowBinade);
    }
}
