using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class RigidPointShadowEvidenceTests
{
    [Fact]
    public void PixelCountsIncludeDifferencesBeyondTheRetainedList()
    {
        byte[] split = new byte[80], full = new byte[80];
        for (int x = 0; x < 20; x++)
        {
            split[x * 4 + 3] = full[x * 4 + 3] = 255;
            full[x * 4] = (byte)(x + 1);
            full[x * 4 + 1] = 2;
        }
        var result = RigidPointShadowEvidence.ComparePixels(split, full, 20, 1);
        Assert.Equal(20, result.ChangedPixels);
        Assert.Equal(40, result.ChangedChannels);
        Assert.Equal(20, result.MaximumDelta);
        Assert.Equal(16, result.FirstPixels.Length);
        Assert.Equal(15, result.FirstPixels[15].X);
        Assert.Equal(0, result.FirstPixels[0].Y);
        Assert.Equal(new[] { 0, 0, 0, 255 }, result.FirstPixels[0].Split);
        Assert.Equal(new[] { 1, 2, 0, 255 }, result.FirstPixels[0].Full);
        Assert.Equal(new[] { 1, 2, 0, 0 }, result.FirstPixels[0].Delta);
    }

    [Fact]
    public void CanonicalCellsUseEachLightRowAndIgnoreUnassignedTransientStorage()
    {
        var split = Atlas([2, 0], [0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f,
            float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
            0.75f, 0.75f, 0.75f, 1f, 0.75f, 0.625f]);
        var transient = Atlas([-1, 1], [0.01f, 0.01f, 0.01f, 0.01f, 0.01f, 0.01f,
            0.4f, 0.4f, 0.4f, 0.4f, 0.4f, 0.4f,
            float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN]);
        var full = Atlas([0, 2], [0.75f, 0.75f, 0.75f, 0.25f, 0.75f, 0.5f,
            float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
            0.4f, 0.4f, 0.4f, 0.4f, 0.4f, 0.4f]);
        var result = RigidPointShadowEvidence.CompareDepth(split, transient, full, retain: 1);
        Assert.Equal(12, result.ComparedTexels);
        Assert.Equal(2, result.MismatchedTexels);
        Assert.Equal(1, result.ClearCoveredDisagreements);
        Assert.Equal(0.75, result.MaximumFiniteAbsoluteDelta);
        Assert.Equal(16_777_216UL, result.MaximumFiniteUlpDelta);
        Assert.Equal(0, result.BaseNonfinite);
        Assert.Equal(0, result.TransientNonfinite);
        var first = Assert.Single(result.FirstTexels);
        Assert.Equal(0, first.Light);
        Assert.Equal(3, first.Face);
        Assert.Equal(0, first.X);
        Assert.Equal(0, first.Y);
        Assert.Null(first.TransientBits);
        Assert.Equal(0x3f800000U, first.CombinedBits);
        Assert.Equal(0x3e800000U, first.FullBits);
    }

    [Fact]
    public void NonfiniteAndSignedZeroBitsSurviveDepthAccounting()
    {
        float nan = BitConverter.Int32BitsToSingle(0x7fc01234);
        var split = Atlas([0], [nan, float.PositiveInfinity, float.NegativeInfinity, 1f, 0.5f, -0f]);
        var full = Atlas([0], [1f, float.PositiveInfinity, float.NegativeInfinity, 0.5f, 0.5f, 0f]);
        var result = RigidPointShadowEvidence.CompareDepth(split, null, full);
        Assert.Equal(3, result.MismatchedTexels);
        Assert.Equal(3, result.BaseNonfinite);
        Assert.Equal(3, result.CombinedNonfinite);
        Assert.Equal(2, result.FullNonfinite);
        Assert.Equal(1, result.ClearCoveredDisagreements);
        Assert.Equal(0.5, result.MaximumFiniteAbsoluteDelta);
        Assert.Equal(8_388_608UL, result.MaximumFiniteUlpDelta);
        Assert.Equal(0x7fc01234U, result.FirstTexels[0].BaseBits);
        Assert.Equal(0x80000000U, result.FirstTexels[2].CombinedBits);
    }

    [Fact]
    public void CanonicalTexelsRetainLocalCoordinatesInsideLargerFaces()
    {
        float[] split = new float[48], full = new float[48];
        Array.Fill(split, 0.75f);
        Array.Fill(full, 0.75f);
        split[45] = 0.5f;
        full[21] = 0.25f;
        var result = RigidPointShadowEvidence.CompareDepth(new(12, 4, 2, [1], split), null, new(12, 4, 2, [0], full));
        Assert.Equal(24, result.ComparedTexels);
        Assert.Equal(1, result.MismatchedTexels);
        Assert.Equal(0.25, result.MaximumFiniteAbsoluteDelta);
        var texel = Assert.Single(result.FirstTexels);
        Assert.Equal(4, texel.Face);
        Assert.Equal(1, texel.X);
        Assert.Equal(1, texel.Y);
        Assert.Equal(0x3f000000U, texel.CombinedBits);
        Assert.Equal(0x3e800000U, texel.FullBits);
    }

    [Fact]
    public void CompressedCellsExcludeSpareRowsAndKeepExactFloatWords()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR")
            ?? Path.GetTempPath(), "rigid-evidence-cpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var atlas = Atlas([2, -1, 0], [0f, -0f, 0.25f, 0.5f, 0.75f, 1f,
                float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f]);
            string file = Path.Combine(root, "cells.r32.gz");
            RigidPointShadowEvidence.WriteCells(file, atlas);
            using var zipped = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var reader = new BinaryReader(zipped);
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(6, reader.ReadInt32());
            Assert.Equal(3, reader.ReadInt32());
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(2, reader.ReadInt32());
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(2, reader.ReadInt32());
            Assert.Equal(new uint[] { 0x3e000000, 0x3e800000, 0x3ec00000, 0x3f000000, 0x3f200000, 0x3f400000 }, Words(reader));
            Assert.Equal(2, reader.ReadInt32());
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(new uint[] { 0, 0x80000000, 0x3e800000, 0x3f000000, 0x3f400000, 0x3f800000 }, Words(reader));
            Assert.Equal(-1, reader.BaseStream.ReadByte());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RepeatedCaseWritesKeepSeparateImagesAndNumericPixelEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "rigid-evidence-source-" + Guid.NewGuid().ToString("N"));
        string? first = null, second = null;
        try
        {
            byte[] split = [0, 2, 3, 255], full = [4, 2, 3, 255];
            string source = Path.Combine(root, "fixture.cs");
            first = RigidPointShadowEvidence.Begin("vulkan-native", "Soft", "Case", "growth", split, full, 1, 1, new { View = Matrix4x4.Identity }, source);
            second = RigidPointShadowEvidence.Begin("vulkan-native", "Soft", "Case", "growth", split, full, 1, 1, new { Pose = -2 }, source);
            Assert.NotEqual(first, second);
            Assert.StartsWith("rigid-point.vulkan-native.Soft.Case.growth.", Path.GetFileName(first));
            string expectedRoot = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR") ?? Path.Combine(root, "goldens-evidence");
            Assert.Equal(Path.GetFullPath(expectedRoot), Path.GetDirectoryName(Path.GetFullPath(first)));
            Assert.True(File.Exists(Path.Combine(first, "split.png")));
            Assert.True(File.Exists(Path.Combine(first, "full.png")));
            Assert.True(File.Exists(Path.Combine(first, "difference-x32.png")));
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "pixels.json")));
            JsonElement pixels = json.RootElement.GetProperty("Pixels");
            Assert.Equal(1, pixels.GetProperty("ChangedPixels").GetInt32());
            Assert.Equal(4, pixels.GetProperty("FirstPixels")[0].GetProperty("Full")[0].GetInt32());
            Assert.Equal(1f, json.RootElement.GetProperty("Metadata").GetProperty("View").GetProperty("M11").GetSingle());
            var atlas = Atlas([0], [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f]);
            RigidPointShadowEvidence.Complete(first, atlas, null, atlas, new { NdcDepthBits = 0x3f000000U });
            Assert.False(File.Exists(Path.Combine(first, "split-transient.r32.gz")));
            using JsonDocument depths = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "depth.json")));
            Assert.Equal(0, depths.RootElement.GetProperty("Depth").GetProperty("MismatchedTexels").GetInt32());
            Assert.Equal(JsonValueKind.Null, depths.RootElement.GetProperty("Transient").ValueKind);
            Assert.Equal(0x3f000000U, depths.RootElement.GetProperty("Receivers").GetProperty("NdcDepthBits").GetUInt32());
        }
        finally
        {
            if (first is not null) Directory.Delete(first, recursive: true);
            if (second is not null) Directory.Delete(second, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReceiverFootprintsPreserveBilinearCoordinatesWeightsAndVOrientation()
    {
        var direct = RigidPointShadowEvidence.ReceiverFootprint(0, 0, 256, 256, 1600, 900, flipV: false);
        Assert.Equal(4, direct.Length);
        Assert.Equal(new RigidPointShadowEvidence.ReceiverSite(2, 1, 0.2783203125), direct[0]);
        Assert.Equal(new RigidPointShadowEvidence.ReceiverSite(3, 1, 0.4638671875), direct[1]);
        Assert.Equal(new RigidPointShadowEvidence.ReceiverSite(2, 2, 0.0966796875), direct[2]);
        Assert.Equal(new RigidPointShadowEvidence.ReceiverSite(3, 2, 0.1611328125), direct[3]);
        var flipped = RigidPointShadowEvidence.ReceiverFootprint(0, 0, 256, 256, 1600, 900, flipV: true);
        Assert.Equal(new RigidPointShadowEvidence.ReceiverSite(2, 897, 0.0966796875), flipped[0]);
        Assert.Equal(new RigidPointShadowEvidence.ReceiverSite(3, 898, 0.4638671875), flipped[3]);
        var clamped = RigidPointShadowEvidence.ReceiverFootprint(0, 0, 1, 1, 1, 1, flipV: false);
        Assert.All(clamped, site => { Assert.Equal(0, site.X); Assert.Equal(0, site.Y); });
        Assert.Equal(1d, clamped[0].NominalWeight);
        Assert.Equal(0d, clamped[3].NominalWeight);
    }

    static uint[] Words(BinaryReader reader) =>
        [reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()];

    static RigidPointShadowEvidence.Atlas Atlas(int[] rows, float[] values) => new(6, values.Length / 6, 1, rows, values);
}
