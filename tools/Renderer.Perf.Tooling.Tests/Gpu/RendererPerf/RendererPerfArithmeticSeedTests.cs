using System;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfArithmeticSeedTests
{
    [Theory]
    [InlineData(9, 0)]
    [InlineData(9, 32768)]
    [InlineData(9, 65535)]
    [InlineData(10, 0)]
    [InlineData(10, 32768)]
    [InlineData(10, 65535)]
    public void Work_identity_changes_when_any_uploaded_seed_bit_changes(int id, int changedIndex)
    {
        float[] seeds = Seeds();
        string original = RendererPerfControls.Definition(id).WorkSha256;
        Assert.Equal(original, RendererPerfControls.Definition(id, seeds).WorkSha256);
        seeds[changedIndex] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(seeds[changedIndex]) ^ 1);
        string changed = RendererPerfControls.Definition(id, seeds).WorkSha256;
        Assert.NotEqual(original, changed);
        using var device = new PostTraceDevice();
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfControls.Create(device, new(id, "1", changed, 64, 128)));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void Work_identity_refuses_incomplete_arithmetic_inputs(int id)
        => Assert.Throws<ArgumentException>(() => RendererPerfControls.Definition(id, Seeds().AsSpan(1)));

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void Hashed_seed_bytes_match_the_actual_cold_upload(int id)
    {
        using var device = new PostTraceDevice();
        using var commands = (GeometryTraceCommands)device.Factory.CreateCommandList();
        device.UploadTrace = commands;
        var definition = RendererPerfControls.Definition(id);
        using var work = RendererPerfControls.Create(device, new(id, "1", definition.WorkSha256, 64, 128));
        work.PrepareCold(-64);
        var upload = Assert.Single(commands.Events, e => e.Op == "device-buffer");
        float[] seeds = Seeds();
        ulong hash = 14695981039346656037UL;
        foreach (byte value in MemoryMarshal.AsBytes(seeds.AsSpan())) hash = unchecked((hash ^ value) * 1099511628211UL);
        Assert.Equal(262144UL, upload.Y);
        Assert.Equal(hash, upload.Data);
        Assert.Equal(definition.WorkSha256, RendererPerfControls.Definition(id, seeds).WorkSha256);
    }

    static float[] Seeds() => Enumerable.Range(0, RendererPerfControls.ArithmeticInvocations)
        .Select(RendererPerfControls.ArithmeticSeed).ToArray();
}
