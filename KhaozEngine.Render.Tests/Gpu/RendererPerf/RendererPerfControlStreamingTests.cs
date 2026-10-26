using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfControlStreamingTests
{
    [Fact]
    public void Large_stream_binds_128_mib_buffers_and_dispatches_every_word_once()
    {
        using var device = new PostTraceDevice();
        var facts = RendererPerfControls.Definition(7);
        using var work = RendererPerfControls.Create(device, new(7, "1", facts.WorkSha256, 32, 64));
        work.PrepareCold(-32);
        using var commands = (GeometryTraceCommands)device.Factory.CreateCommandList();

        commands.Begin(); work.Record(commands, -32); commands.End();

        var binding = Assert.Single(commands.Events, e => e.Op == "compute-set");
        var resources = Assert.IsType<FakeResourceSet>(commands.Resource(binding.A)).Resources;
        Assert.Equal(2, resources.Length);
        Assert.All(resources, resource => Assert.Equal(134217728u, Assert.IsAssignableFrom<IGpuBuffer>(resource).SizeInBytes));
        var dispatch = Assert.Single(commands.Events, e => e.Op == "dispatch");
        Assert.Equal(1024UL, dispatch.X);
        Assert.Equal(128UL, dispatch.Y);
        Assert.Equal(1UL, dispatch.Z);
        Assert.Equal(134217728, work.ControlMetrics["control.readBytes"].Value);
        Assert.Equal(134217728, work.ControlMetrics["control.writeBytes"].Value);
    }

    [Fact]
    public void Large_stream_validates_all_128_mib_and_refuses_a_missing_or_corrupt_last_word()
    {
        uint[] words = new uint[33554432];
        Array.Fill(words, 0xb791a5a5u);

        Assert.Equal(64, RendererPerfControls.ValidateWords(7, words).Length);
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.ValidateWords(7, words.AsSpan(0, words.Length - 1)));
        words[^1] ^= 1;
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.ValidateWords(7, words));
    }
}
