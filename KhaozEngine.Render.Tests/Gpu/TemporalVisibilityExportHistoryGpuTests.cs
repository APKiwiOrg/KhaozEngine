using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityExportHistoryGpuTests : TemporalResolveGpuFacts
{
    [GpuFact]
    public void Production_colour_replaces_export_metadata_without_copying_its_state()
    {
        using var production = new Rig();
        using var exporting = new Rig();
        float[] colour = Grey((x, y) => Q(0.125f + 0.01f * x + 0.02f * y));
        float[] metadata = Grey((_, _) => 100f);
        float[] state = StateWithRecord(N, N, (_, _) => new Vector4(0.5f, 0.25f, -0.015625f, 0.03125f));
        TemporalTextureIo.Upload(production.Device, production.History.Color(production.History.WriteIndex), colour);
        TemporalTextureIo.Upload(production.Device, production.History.Confidence(production.History.WriteIndex),
            StateWithRecord(N, N, (_, _) => new Vector4(0.75f, 0.875f, 0.125f, 0.25f)));
        TemporalTextureIo.Upload(exporting.Device, exporting.History.Color(exporting.History.WriteIndex), metadata);
        TemporalTextureIo.Upload(exporting.Device, exporting.History.Confidence(exporting.History.WriteIndex), state);
        float[] sourceBefore = production.ReadColor(), exportBefore = exporting.ReadColor();
        float[] stateBefore = exporting.ReadState(), productionStateBefore = production.ReadState();
        Assert.False(sourceBefore.AsSpan().SequenceEqual(exportBefore));

        using var history = new TemporalVisibilityExportHistory(production.Device, production.History,
            exporting.Device, exporting.History);
        history.CopyPreviousColour();

        Assert.Equal(MemoryMarshal.AsBytes(sourceBefore.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(exporting.ReadColor().AsSpan()).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(stateBefore.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(exporting.ReadState().AsSpan()).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(sourceBefore.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(production.ReadColor().AsSpan()).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(productionStateBefore.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(production.ReadState().AsSpan()).ToArray());
    }
}
