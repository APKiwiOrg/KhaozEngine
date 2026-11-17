using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// A still walk's stored history from one compiled temporal program against another's, frame by frame, held to what
/// main asserts between two compiled programs on the running device (<see cref="TemporalEntryIdentityGpuTests"/>).
/// On llvmpipe that is the entry pair's rules: <see cref="TemporalEntryStateComparison"/> bounds drift and swing and
/// keeps confidence, locks and marks exact, and the colour history is held to
/// <see cref="TemporalSoftwareCompileDivergenceRule"/> over the walk. Main asserts no presented frame between its
/// entries there, so neither does this: the stored history is the contract and the picture is a function of it.
/// Where the expected side skipped the visibility decision, a frame compares a certified program with a checked one,
/// which <see cref="TemporalFullyLitHistoryOracle"/> rules on for the device, colour included. Every other frame on
/// every other device compares colour, state and any presented frame exactly.
/// </summary>
internal sealed class TemporalStillHistoryComparison(ITestOutputHelper output, IGpuDevice device, string label)
{
    /// <summary>One side of the comparison: the device, the history it stored into and the renderer that ran.</summary>
    internal readonly record struct Side(IGpuDevice Device, TemporalHistory History, TemporalResolveRenderer Renderer)
    {
        internal static Side Of(TemporalFixture fx) => new(fx.Device, fx.Scene.TemporalHistory, fx.Scene.TemporalResolveRendererForTests!);

        internal float[] Colour() => TemporalTextureIo.Read(Device, History.Color(History.WriteIndex));
        internal float[] State() => TemporalTextureIo.Read(Device, History.Confidence(History.WriteIndex));
        internal TemporalFullyLitHistoryOracle.Snapshot Snapshot() => TemporalFullyLitHistoryOracle.Snapshot.Read(Device, History, Renderer);
    }

    readonly TemporalEntryStateComparison _state = new(TemporalDeviceClass.IsLavapipe(device));
    readonly TemporalFullyLitHistoryOracle _certificate = TemporalFullyLitHistoryOracle.For(output, device);
    readonly TemporalEntryDifference _colour = new($"{label} colour");
    long _colourCompared;

    /// <summary>Compare one frame. The actual side runs one checked program for every certificate state, so a
    /// certified frame can only come from the expected side. The presented frames, when given, are compared exactly
    /// wherever the colour history is.</summary>
    internal void Frame(int frame, Side expected, Side actual, byte[]? expectedPresented = null, byte[]? actualPresented = null)
    {
        bool certified = expected.Renderer.LastVisibilityDecisionSkipped;
        Assert.False(actual.Renderer.LastVisibilityDecisionSkipped);
        if (_state.BoundsRecordChannels)
        {
            float[] left = expected.Colour(), right = actual.Colour();
            Assert.Equal(left.Length, right.Length);
            for (int i = 0; i < left.Length; i++) _colour.Add(frame, left[i], right[i]);
            _colourCompared += left.Length;
        }
        else if (!certified || _certificate.HoldsCertifiedAgainstCheckedExact)
        {
            if (expectedPresented is not null || actualPresented is not null) Assert.Equal(expectedPresented, actualPresented);
            TemporalHistorySample.Exact(frame, $"{label} history colour", expected.Colour(), actual.Colour());
        }
        if (certified && !_state.BoundsRecordChannels)
            _certificate.Report(frame, $"{label} certified vs checked", expected.Snapshot(), actual.Snapshot());
        else
            _state.Compare(frame, $"{label} history state", expected.State(), actual.State());
    }

    /// <summary>Assert the walk's bounded totals once every frame is compared.</summary>
    internal void AssertComplete()
    {
        if (_state.BoundsRecordChannels)
        {
            double share = TemporalSoftwareCompileDivergenceRule.ShareOf(_colour.Values, _colourCompared);
            output.WriteLine($"  software Vulkan: {_colour}, within {TemporalSoftwareCompileDivergenceRule.AbsoluteBound}"
                + $" on {share:P4} of {_colourCompared} values");
            Assert.True(TemporalSoftwareCompileDivergenceRule.Allows(_colour.Worst, _colour.Values, _colourCompared),
                $"{label}: the still colour on a software Vulkan device left its bound "
                + $"({TemporalSoftwareCompileDivergenceRule.AbsoluteBound}, "
                + $"{TemporalSoftwareCompileDivergenceRule.DifferingShareBound:P2} of values): {_colour}");
        }
        _state.AssertComplete(output, $"{label} still");
        _certificate.AssertComplete();
    }
}
