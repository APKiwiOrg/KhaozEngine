using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalShaderRefusalTests
{
    static MetalShaderIndexTable EmptyTable() => MetalShaderIndexTable.Build([], [], "empty");

    [Fact]
    public void MissingAndReleasedFunctionsAreDistinctWithoutADevice()
    {
        var liveness = new DeviceLiveness();
        var table = EmptyTable();
        using var shaders = new MetalShaderSet(liveness, [new(MetalShaderStage.Vertex, default, default)], table);
        Assert.True(shaders.FunctionFor(MetalShaderStage.Vertex).IsNull);
        var missing = Assert.Throws<MetalShaderFunctionException>(() => shaders.FunctionFor(MetalShaderStage.Fragment));
        Assert.IsAssignableFrom<InvalidOperationException>(missing);
        Assert.Equal(MetalShaderFunctionRefusal.StageMissing, missing.Reason);
        Assert.Equal(GpuShaderStages.Fragment, missing.Stage);
        Assert.Equal("shader set", missing.ResourceKind);
        var invalidStage = Assert.Throws<MetalShaderFunctionException>(() => shaders.FunctionFor((MetalShaderStage)99));
        Assert.Equal(MetalShaderFunctionRefusal.StageMissing, invalidStage.Reason);

        // Dead ownership prevents disposal of the intentionally nil native handles on macOS too.
        liveness.MarkDead();
        shaders.Dispose();
        var released = Assert.Throws<MetalShaderFunctionException>(() => shaders.FunctionFor(MetalShaderStage.Vertex));
        Assert.Equal(MetalShaderFunctionRefusal.FunctionReleased, released.Reason);
        Assert.Equal(GpuShaderStages.Vertex, released.Stage);
        Assert.Equal("shader set", released.ResourceKind);

        using var compute = new MetalComputeShader(liveness, default, table, 1, 1, 1);
        Assert.True(compute.Function.IsNull);
        compute.Dispose();
        var kernel = Assert.Throws<MetalShaderFunctionException>(() => compute.Function);
        Assert.Equal(MetalShaderFunctionRefusal.FunctionReleased, kernel.Reason);
        Assert.Equal(GpuShaderStages.Compute, kernel.Stage);
        Assert.Equal("compute shader", kernel.ResourceKind);
    }

    [Theory]
    [InlineData("MTLCompileOptions")]
    [InlineData("MTLRenderPipelineDescriptor")]
    [InlineData("MTLVertexDescriptor")]
    [InlineData("MTLDepthStencilDescriptor")]
    public void MissingNativeClassesRetainTheirIdentity(string nativeClass)
    {
        MetalShaderNativeValidation.RequireClassAvailable(true, nativeClass, nativeClass == "MTLCompileOptions");
        var error = Assert.Throws<ShaderValidationStateException>(() =>
            MetalShaderNativeValidation.RequireClassAvailable(false, nativeClass, nativeClass == "MTLCompileOptions"));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(ShaderValidationRefusal.MetalNativeClassUnavailable, error.Reason);
        Assert.Equal(nativeClass, error.NativeClass);
    }

    [Fact]
    public void MissingDepthStateIsSeparateFromMissingDescriptorClass()
    {
        MetalShaderNativeValidation.RequireDepthStateAvailable(true);
        var error = Assert.Throws<ShaderValidationStateException>(() => MetalShaderNativeValidation.RequireDepthStateAvailable(false));
        Assert.Equal(ShaderValidationRefusal.MetalDepthStateUnavailable, error.Reason);
        Assert.Equal("MTLDepthStencilState", error.NativeClass);
    }

    [Fact]
    public void ProgramAndResourceUseStagesMustExist()
    {
        var program = new MetalMslProgram([new(MetalShaderStage.Vertex, "main0", "vertex void main0() {}")], EmptyTable());
        Assert.Equal(MetalShaderStage.Vertex, program.StageOf(MetalShaderStage.Vertex).Stage);
        var missing = Assert.Throws<ShaderValidationException>(() => program.StageOf(MetalShaderStage.Fragment));
        Assert.Equal(ShaderValidationRefusal.MetalProgramStageMissing, missing.Reason);
        Assert.Equal(GpuShaderStages.Fragment, missing.Stage);

        Assert.Empty(MetalShaderBuild.UseFor([new(GpuShaderStages.Vertex, [])], MetalShaderStage.Vertex, "uses"));
        var use = Assert.Throws<ShaderValidationException>(() => MetalShaderBuild.UseFor([], MetalShaderStage.Vertex, "uses"));
        Assert.Equal(ShaderValidationRefusal.MetalResourceUseListMissing, use.Reason);
        Assert.Equal("uses", use.Label);
        Assert.Equal(GpuShaderStages.Vertex, use.Stage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("native compute rejection")]
    public void ComputePipelineRejectionCarriesTheNativeDiagnosticOrItsAbsence(string? diagnostic)
    {
        MetalShaderNativeValidation.RequireComputePipelineAvailable(true, null);
        var error = Assert.Throws<ShaderValidationException>(() =>
            MetalShaderNativeValidation.RequireComputePipelineAvailable(false, diagnostic));
        Assert.Equal(ShaderValidationRefusal.MetalComputePipelineRejected, error.Reason);
        Assert.Equal("compute", error.PipelineKind);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Equal(diagnostic is null, error.NativeDiagnosticMissing);
    }
}
