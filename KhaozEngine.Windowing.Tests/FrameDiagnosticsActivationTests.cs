using System;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class FrameDiagnosticsActivationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unset_path_creates_no_recorder_and_never_reads_annotation_channels(string? value)
    {
        string? previous = Environment.GetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH");
        var passes = MetalFrameTimingProbe.PassTiming;
        try
        {
            Environment.SetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH", value);
            Assert.False(FrameDiagnosticsRecorder.IsEnabled);
            Assert.Null(FrameDiagnosticsRecorder.FromEnvironment(new RefusedAnnotation(), gbufferGates: new RefusedGates()));
            Assert.False(MetalFrameTimingProbe.Sampling);
            Assert.Same(passes, MetalFrameTimingProbe.PassTiming);
        }
        finally { Environment.SetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH", previous); }
    }

    sealed class RefusedAnnotation : IFrameDiagnosticsAnnotator
    {
        public string[] Channels => throw new InvalidOperationException("Disabled diagnostics must never register channels.");
        public void Sample(Span<long> values) => throw new InvalidOperationException();
    }

    sealed class RefusedGates : IFrameDiagnosticsGBufferGates
    {
        public void Apply(GBufferGate gate) => throw new InvalidOperationException();
        public int ConsumeApplied(Span<long> skippedDraws) => throw new InvalidOperationException();
    }
}
