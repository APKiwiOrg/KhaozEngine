using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class ShaderNativeResultRefusalTests
{
    [Theory]
    [InlineData(ShaderValidationRefusal.FrontEndCompilerInitializationFailed, "compiler")]
    [InlineData(ShaderValidationRefusal.FrontEndOptionsInitializationFailed, "compile options")]
    [InlineData(ShaderValidationRefusal.FrontEndResultMissing, "result")]
    public void MissingFrontEndHandlesKeepTheirOriginalBaseType(ShaderValidationRefusal reason, string kind)
    {
        void Check(bool available)
        {
            if (kind == "compiler") SpirvFrontEnd.RequireCompilerInitialized(available);
            else if (kind == "compile options") SpirvFrontEnd.RequireOptionsInitialized(available);
            else SpirvFrontEnd.RequireCompileResultReturned(available);
        }

        Check(true);
        var error = Assert.Throws<ShaderValidationStateException>(() => Check(false));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(reason, error.Reason);
        if (kind == "result") Assert.Equal("shaderc_compile_into_spv", error.NativeOperation);
        else Assert.Equal(kind, error.NativeObjectKind);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    public void ContextCreationRefusesAnErrorOrMissingHandle(int result, bool created)
    {
        SpirvCrossCompile.RequireContextCreated(0, true, "context");
        var error = Assert.Throws<ShaderValidationException>(
            () => SpirvCrossCompile.RequireContextCreated(result, created, "context"));
        Assert.Equal(ShaderValidationRefusal.CrossContextCreationFailed, error.Reason);
        Assert.Equal("context", error.Label);
        Assert.Equal("spvc_context_create", error.Operation);
        Assert.Equal(result, error.ResultCode);
    }

    [Fact]
    public void NativeCrossFailureRetainsTheOperationCodeAndDiagnostic()
    {
        SpirvCrossCompile.RequireNativeCallSucceeded(0, "", "cross", "parse");
        var error = Assert.Throws<ShaderValidationException>(
            () => SpirvCrossCompile.RequireNativeCallSucceeded(-1, "native detail\n", "cross", "parse"));
        Assert.Equal(ShaderValidationRefusal.CrossNativeCallFailed, error.Reason);
        Assert.Equal("cross", error.Label);
        Assert.Equal("parse", error.Operation);
        Assert.Equal(-1, error.ResultCode);
        Assert.Equal("native detail\n", error.Diagnostic);
    }

    [Fact]
    public void FxcOutputAndVertexReflectionRejectEmptyModulesBeforeInterop()
    {
        D3D11ShaderResultValidation.RequireFxcOutput(1, "cs_5_0", "output");
        var output = Assert.Throws<ShaderValidationException>(
            () => D3D11ShaderResultValidation.RequireFxcOutput(0, "cs_5_0", "output"));
        Assert.Equal(ShaderValidationRefusal.D3D11FxcOutputEmpty, output.Reason);
        Assert.Equal("output", output.Label);
        Assert.Equal("cs_5_0", output.Profile);

        D3D11ShaderResultValidation.RequireVertexModule(1, "vertex");
        var vertex = Assert.Throws<ShaderValidationException>(
            () => D3D11ShaderResultValidation.RequireVertexModule(0, "vertex"));
        Assert.Equal(ShaderValidationRefusal.D3D11VertexModuleEmpty, vertex.Reason);
        Assert.Equal("vertex", vertex.Label);
    }

    [Fact]
    public void VertexReflectionKeepsTheActualNativeFailure()
    {
        var native = new InvalidOperationException("native reflection failure");
        var error = Assert.Throws<ShaderValidationException>(
            () => D3D11ShaderResultValidation.ThrowVertexReflectionFailure(native, "vertex"));
        Assert.Equal(ShaderValidationRefusal.D3D11VertexReflectionFailed, error.Reason);
        Assert.Equal("vertex", error.Label);
        Assert.Same(native, error.InnerException);
        Assert.Equal("native reflection failure", error.Diagnostic);
    }
}
