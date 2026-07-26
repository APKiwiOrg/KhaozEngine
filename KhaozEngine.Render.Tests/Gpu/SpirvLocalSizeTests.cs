using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Veldrid;
using Veldrid.SPIRV;
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
        static byte[] Spirv(string glsl)
            => SpirvCompilation.CompileGlslToSpirv(glsl, "t", ShaderStages.Compute, GlslCompileOptions.Default).SpirvBytes;

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

        [Fact]
        public void NonSpirvBytesAreRejectedByTheMagicNumber()
        {
            var notSpirv = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
            var ex = Assert.Throws<ShaderValidationException>(() => SpirvLocalSize.Parse(notSpirv, "t"));
            Assert.Contains("not a SPIR-V module", ex.Message);
        }

        [Fact]
        public void ATruncatedModuleIsRejectedRatherThanReadPastTheEnd()
        {
            byte[] spirv = Spirv("#version 450\nlayout(local_size_x = 64) in;\nvoid main() {}\n");
            var truncated = new byte[24];                  // header (20 bytes) plus one partial instruction word
            System.Array.Copy(spirv, truncated, truncated.Length);
            Assert.Throws<ShaderValidationException>(() => SpirvLocalSize.Parse(truncated, "t"));
        }
    }
}
