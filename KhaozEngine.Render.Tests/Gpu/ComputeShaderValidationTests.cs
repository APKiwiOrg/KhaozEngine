using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Device-free validation of every compute shader in the proof suite, through
    /// <see cref="ShaderValidation.ValidateCompute"/>. Plain <c>[Fact]</c>s, not <c>[GpuFact]</c>s, so a compute
    /// source that fails to cross-compile to one backend fails the fast GPU-free lane on every push instead of
    /// surfacing only on that backend's GPU leg. The compute mirror of
    /// <see cref="ShaderSourceValidationTests"/>.
    /// </summary>
    public sealed class ComputeShaderValidationTests
    {
        [Fact]
        public void Reduce() => ShaderValidation.ValidateCompute(ComputeShaders.Reduce, "Reduce");

        [Fact]
        public void WriteImage() => ShaderValidation.ValidateCompute(ComputeShaders.WriteImage, "WriteImage");

        [Fact]
        public void FftStage() => ShaderValidation.ValidateCompute(ComputeShaders.FftStage, "FftStage");

        [Fact]
        public void ASyntaxErrorIsReportedWithTheLabelAndTheStage()
        {
            var ex = Assert.Throws<ShaderValidationException>(() => ShaderValidation.ValidateCompute(
                "#version 450\nlayout(local_size_x = 1) in;\nvoid main() { this is not glsl }\n", "Broken"));
            Assert.Contains("Broken", ex.Message);
            Assert.Contains("Compute", ex.Message);
        }
    }
}
