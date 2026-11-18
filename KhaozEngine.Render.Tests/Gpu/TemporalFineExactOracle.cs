using System;
using System.Security.Cryptography;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Hash the physical payload, excluding row padding, and report the first changed texel per target.</summary>
internal sealed class TemporalFineExactOracle : IDisposable
{
    readonly IGpuDevice _device;
    readonly IGpuCommandList _commands;
    readonly TemporalFixture _control, _trial;
    readonly IGpuTexture[] _left, _right;
    static readonly string[] Targets = ["R", "state", "atlas", "presented", "depth", "visibility", "classification",
        "child0", "child1", "child2", "child3"];

    internal TemporalFineExactOracle(IGpuDevice device, TemporalFixture control, TemporalFixture trial)
    {
        _device = device;
        _control = control;
        _trial = trial;
        _commands = device.Factory.CreateCommandList();
        var sources = Sources(control);
        _left = Array.ConvertAll(sources, Stage);
        _right = Array.ConvertAll(sources, Stage);
    }

    IGpuTexture Stage(IGpuTexture source) => _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
        source.Width, source.Height, source.Format, GpuTextureUsage.Staging));

    static IGpuTexture[] Sources(TemporalFixture fixture)
    {
        var history = fixture.Scene.TemporalHistory;
        int write = history.WriteIndex;
        var detail = history.DetailStore!;
        return [history.Color(write), history.Confidence(write), detail.Color(write), fixture.PresentedTexture,
            history.PreviousDepth(write), fixture.Scene.TemporalResolveRendererForTests!.VisibilityHistory.Texture(write),
            detail.ClassificationForTests, detail.ChildForTests(0), detail.ChildForTests(1),
            detail.ChildForTests(2), detail.ChildForTests(3)];
    }

    internal string[] Compare()
    {
        IGpuTexture[] control = Sources(_control), trial = Sources(_trial);
        using (GpuRecording.Open(_device, _commands, "TemporalFineExactOracle.Compare"))
        {
            for (int i = 0; i < Targets.Length; i++)
            {
                _commands.CopyTexture(control[i], _left[i]);
                _commands.CopyTexture(trial[i], _right[i]);
            }
        }
        _device.Submit(_commands);
        _device.WaitForIdle();
        var results = new string[Targets.Length];
        for (int i = 0; i < results.Length; i++)
            results[i] = Targets[i] + "|" + Compare(_left[i], _right[i]);
        return results;
    }

    unsafe string Compare(IGpuTexture left, IGpuTexture right)
    {
        var a = _device.Map(left, GpuMapMode.Read);
        try
        {
            var b = _device.Map(right, GpuMapMode.Read);
            try
            {
                using var hashA = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int stride = left.Format switch
                {
                    GpuPixelFormat.R16G16B16A16Float => 8,
                    GpuPixelFormat.R32Float or GpuPixelFormat.R8G8B8A8UNorm => 4,
                    GpuPixelFormat.R8UNorm => 1,
                    _ => throw new InvalidOperationException($"Unexpected oracle format {left.Format}."),
                };
                int rowSize = checked((int)left.Width * stride);
                string? difference = null;
                for (int y = 0; y < left.Height; y++)
                {
                    var rowA = new ReadOnlySpan<byte>((byte*)a.Data + y * a.RowPitch, rowSize);
                    var rowB = new ReadOnlySpan<byte>((byte*)b.Data + y * b.RowPitch, rowSize);
                    hashA.AppendData(rowA);
                    if (difference is null && !rowA.SequenceEqual(rowB))
                    {
                        int i = 0;
                        while (rowA[i] == rowB[i]) i++;
                        int x = i / stride;
                        var pa = rowA.Slice(x * stride, stride);
                        var pb = rowB.Slice(x * stride, stride);
                        difference = $"different|xy={x},{y}|reference={Convert.ToHexString(pa)}|variant={Convert.ToHexString(pb)}";
                    }
                }
                string hash = Convert.ToHexString(hashA.GetHashAndReset());
                string trialHash = difference is null ? hash : Hash(b, rowSize, (int)right.Height);
                return $"sha256={hash},{trialHash}|{difference ?? "equal"}";
            }
            finally { _device.Unmap(right); }
        }
        finally { _device.Unmap(left); }
    }

    static unsafe string Hash(MappedData map, int rowBytes, int rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < rows; y++)
            hash.AppendData(new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, rowBytes));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        _commands.Dispose();
        foreach (var target in _left) target.Dispose();
        foreach (var target in _right) target.Dispose();
    }
}
