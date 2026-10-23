using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageFilterQueueShaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deliberate_queue_refusals_compile_with_precise_access_on_each_mode(bool flush)
    {
        foreach (var stage in new[] { CoverageGpuFilteredStage.Faces, CoverageGpuFilteredStage.Pairs })
            foreach (string condition in new[] { "false", "gl_GlobalInvocationID.x == 4u", "true" })
            {
                string source = CoverageGpuFilteredSources.Source(stage).Replace("bool CovFilterRefused = false;",
                    "bool CovFilterRefused = " + condition + ";", StringComparison.Ordinal);
                var options = new GpuShaderCompileOptions(PreciseMath: true, RequireFloat32Eft: true, FlushFloat32Denorms: flush);
                byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "FilteredQueueBoundary", options);
                CoverageSpirvAccessAudit.Validate(spirv, "FilteredQueueBoundary", constantPrivateArrays: true);
                ShaderValidation.ValidateCompute(source, options, "FilteredQueueBoundary");
            }
    }
}
