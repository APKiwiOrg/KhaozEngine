using System;
using System.Buffers.Binary;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless coverage for <see cref="SpirvLocalSize"/>, the reason the compute seam never asks a caller to
    /// restate a shader's workgroup size in C#. The size is read straight out of the compiled module, so the
    /// <c>ThreadGroupSize</c> that Metal needs at dispatch-encode time cannot drift from the GLSL. No device
    /// involved: these compile GLSL to SPIR-V on the CPU and parse the bytes.
    /// </summary>
    public sealed class SpirvLocalSizeTests
    {
        // Through the engine's own front-end seat, which is also the only one there is since the toolchain swap
        // in 18.0.0: these used to call the outgoing library directly under its defaults.
        static byte[] Spirv(string glsl) => SpirvFrontEnd.ToSpirv(glsl, GpuShaderStages.Compute, "t");

        [Theory]
        [InlineData("layout(local_size_x = 256) in;", 256u, 1u, 1u)]
        [InlineData("layout(local_size_x = 8, local_size_y = 8) in;", 8u, 8u, 1u)]
        [InlineData("layout(local_size_x = 4, local_size_y = 2, local_size_z = 3) in;", 4u, 2u, 3u)]
        [InlineData("layout(local_size_x = 1) in;", 1u, 1u, 1u)]
        public void ReadsTheDeclaredWorkgroupSize(string layout, uint x, uint y, uint z)
        {
            byte[] spirv = Spirv($"#version 450\n{layout}\nvoid main() {{}}\n");
            Assert.Equal((x, y, z), SpirvLocalSize.Parse(spirv, "t"));
        }

        // Pins what actually happens when a compute source omits the layout, which is NOT an error: GLSL's default
        // workgroup size is 1x1x1, and glslang emits an explicit LocalSize 1 1 1 execution mode for it. So the
        // "no LocalSize found" throw below is unreachable through the seam's only compile path, and the real
        // hazard is a silent 1x1x1 dispatch (one invocation per group) rather than a missing declaration. Declare
        // the size explicitly for throughput; this test is here so a toolchain change that stopped emitting the
        // default would be caught rather than turning into a hang of a throw nobody expected.
        [Fact]
        public void AnOmittedLayoutYieldsTheGlslDefaultOfOneByOneByOne()
        {
            byte[] spirv = Spirv("#version 450\nvoid main() {}\n");
            Assert.Equal((1u, 1u, 1u), SpirvLocalSize.Parse(spirv, "t"));
        }

        // A real module whose ONLY defect is its magic number. Without the magic check these bytes parse to 64x1x1,
        // so the refusal cannot come from the missing-LocalSize or truncation throws, which share the exception type.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NonSpirvBytesAreRejectedByTheMagicNumber(bool decorations)
        {
            byte[] module = Spirv("#version 450\nlayout(local_size_x = 64) in;\nvoid main() {}\n");
            Assert.Equal((64u, 1u, 1u), SpirvLocalSize.Parse(module, "t"));
            var notSpirv = (byte[])module.Clone();
            notSpirv[0] ^= 0xFF;
            var error = Assert.Throws<ShaderValidationException>(() => Read(notSpirv, decorations));
            Assert.Equal(ShaderValidationRefusal.SpirvMagicInvalid, error.Reason);
            Assert.Equal("t", error.Label);
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(notSpirv), error.ActualMagic);
            Assert.Equal(0x07230203u, error.ExpectedMagic);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ATruncatedModuleIsRejectedRatherThanReadPastTheEnd(bool decorations)
        {
            byte[] spirv = Spirv("#version 450\nlayout(local_size_x = 64) in;\nvoid main() {}\n");
            var truncated = new byte[24];                  // header (20 bytes) plus one partial instruction word
            System.Array.Copy(spirv, truncated, truncated.Length);
            var error = Assert.Throws<ShaderValidationException>(() => Read(truncated, decorations));
            Assert.Equal(ShaderValidationRefusal.SpirvInstructionMalformed, error.Reason);
            Assert.Equal("t", error.Label);
            Assert.Equal(5, error.WordOffset);
            Assert.Equal((int)(BinaryPrimitives.ReadUInt32LittleEndian(truncated.AsSpan(20)) >> 16), error.WordCount);
            Assert.Equal(6, error.ModuleWordCount);
        }

        [Theory]
        [InlineData(false, 16)]
        [InlineData(true, 16)]
        [InlineData(false, 21)]
        [InlineData(true, 21)]
        public void InvalidModuleLengthsNameTheirLayout(bool decorations, int length)
        {
            var error = Assert.Throws<ShaderValidationException>(() => Read(new byte[length], decorations));
            Assert.Equal(ShaderValidationRefusal.SpirvLengthInvalid, error.Reason);
            Assert.Equal("t", error.Label);
            Assert.Equal(length, error.ByteLength);
            Assert.Equal(20, error.MinimumHeaderBytes);
            Assert.Equal(4, error.WordBytes);
        }

        [Theory]
        [InlineData(false, ShaderValidationRefusal.ComputeLocalSizeMissing)]
        [InlineData(true, ShaderValidationRefusal.ComputeLocalSizeSpecialized)]
        public void ExternalModulesMustDeclareALiteralLocalSize(bool specialized, ShaderValidationRefusal reason)
        {
            uint[] words = specialized
                ? [0x07230203, 0x00010000, 0, 8, 0, (6u << 16) | 331u, 1, 38, 2, 3, 4]
                : [0x07230203, 0x00010000, 0, 8, 0];
            var bytes = new byte[words.Length * 4];
            for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
            var error = Assert.Throws<ShaderValidationException>(() => SpirvLocalSize.Parse(bytes, "external"));
            Assert.Equal(reason, error.Reason);
            Assert.Equal("external", error.Label);
            if (specialized) Assert.Equal("LocalSizeId", error.ExecutionMode);
        }

        static void Read(byte[] bytes, bool decorations)
        {
            if (decorations) _ = SpirvResourceDecorations.Read(bytes, "t");
            else _ = SpirvLocalSize.Parse(bytes, "t");
        }
    }
}
