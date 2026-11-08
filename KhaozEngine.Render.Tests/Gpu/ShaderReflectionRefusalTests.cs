using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class ShaderReflectionRefusalTests
{
    const string Vertex = "#version 450\nlayout(location=0) in vec2 p; void main() { gl_Position = vec4(p,0,1); }";
    const string Fragment = "#version 450\nlayout(location=0) out vec4 o; void main() { o = vec4(1); }";

    [Fact]
    public void CombinedSamplerIsSeparateFromTheSupportedTextureSamplerPair()
    {
        const string source = "#version 450\nlayout(set=0,binding=0) uniform sampler2D t; "
            + "layout(location=0) out vec4 o; void main() { o = texture(t,vec2(0)); }";
        SpirvCrossCompile.GlslPairToHlsl(Vertex, Fragment, "reflection");
        var wrapper = Assert.Throws<ShaderValidationException>(() => SpirvCrossCompile.GlslPairToHlsl(Vertex, source, "reflection"));
        Assert.Equal(ShaderValidationRefusal.CrossCompileFailed, wrapper.Reason);
        var error = Assert.IsType<ShaderValidationException>(wrapper.InnerException);
        Assert.Equal(ShaderValidationRefusal.ReflectionResourceKindUnsupported, error.Reason);
        Assert.Equal("reflection", error.Label);
        Assert.NotNull(error.NativeResourceKind);
        Assert.Equal("a combined image sampler (GLSL 'uniform sampler2D')", error.SourceDeclaration);
    }

    [Fact]
    public void IntegerVertexInputsAreRefusedByTheFormatMirror()
    {
        const string source = "#version 450\nlayout(location=0) in ivec2 p; void main() { gl_Position = vec4(p,0,1); }";
        SpirvCrossCompile.GlslPairToHlsl(Vertex, Fragment, "reflection");
        var wrapper = Assert.Throws<ShaderValidationException>(() => SpirvCrossCompile.GlslPairToHlsl(source, Fragment, "reflection"));
        Assert.Equal(ShaderValidationRefusal.CrossCompileFailed, wrapper.Reason);
        var error = Assert.IsType<ShaderValidationException>(wrapper.InnerException);
        Assert.Equal(ShaderValidationRefusal.ReflectionVertexFormatUnsupported, error.Reason);
        Assert.Equal("reflection", error.Label);
        Assert.NotNull(error.NativeBaseType);
        Assert.Equal(2u, error.ComponentCount);
    }
}
