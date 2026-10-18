using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceCrossingTests
{
    [Theory]
    [InlineData(-74)]
    [InlineData(-75)]
    public void Subnormal_opposite_determinants_use_the_scaled_rational_parameter(int exponent)
    {
        // Exact sides are unit squared and minus twice that value. The independent rational quotients are 1/3
        // and 2/3. Exponent -74 gives representable subnormals, -75 also tests underflow before normalization.
        float unit = MathF.ScaleB(1, exponent);
        Vector3 s = Vector3.Zero, t = new(unit, 0, 0), a = new(0, unit, 0), b = new(unit, -2 * unit, 0);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3eaaaaab), CoverageReferencePredicates.CrossingParameter(s, t, a, b));
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3f2aaaab), CoverageReferencePredicates.CrossingParameter(s, t, b, a));
    }

    [Fact]
    public void Every_fixture_reference_output_is_compared_byte_for_byte_when_requested()
    {
        string? directory = Environment.GetEnvironmentVariable("COVERAGE_REFERENCE_SNAPSHOT");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var c in CoverageReferenceCases.All())
        {
            var scratch = new CoverageReferenceScratch(c.Topology);
            var result = new ModelVertex[c.Topology.VertexCount];
            var capture = CoverageExactCaptureTests.Read(c.Id);
            for (int mode = 0; mode < 2; mode++)
            {
                double[] target = mode == 0 ? [] : capture.ClearanceCoverage.Zip(capture.SlackCoverage, (a, b) => a + b).ToArray();
                Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands,
                    scratch, result, out var failure, target, mode == 0 ? [] : capture.CorrectionClearance,
                    mode == 0 ? [] : capture.CorrectionConversion), failure.ToString());
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
                    foreach (var vertex in result)
                        foreach (float field in CoverageReferenceBits.Fields(vertex)) writer.Write(field);
                string path = Path.Combine(directory, $"{c.Id}-{mode}.bin");
                if (File.Exists(path)) Assert.Equal(File.ReadAllBytes(path), bytes.ToArray());
                else File.WriteAllBytes(path, bytes.ToArray());
            }
        }
    }
}
