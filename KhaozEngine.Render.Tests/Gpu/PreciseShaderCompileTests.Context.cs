using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class PreciseShaderCompileTests
{
    [Fact]
    public void Compiler_context_copy_preserves_every_public_identifier()
    {
        var original = new ShaderValidationException(ShaderValidationRefusal.PreciseMslHelperUnsupported, "original");
        var properties = typeof(ShaderValidationException).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var identifiers = properties.Where(p => p.CanWrite).ToArray();
        Assert.True(identifiers.Length > 40);
        foreach (var property in identifiers)
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value = type == typeof(string) ? "original-" + property.Name
                : type == typeof(int) ? 17
                : type == typeof(uint) ? 23u
                : type == typeof(bool) ? true
                : type.IsEnum ? Enum.GetValues(type).GetValue(0)!
                : type == typeof(System.Collections.Generic.IReadOnlyList<string>) ? new[] { "one", "two" }
                : throw new NotSupportedException("Add a distinct copy marker for " + property.Name);
            property.SetValue(original, value);
        }
        var copied = original.WithContext("wrapped", "new-label", GpuShaderStages.Compute, "new-target");
        Assert.Equal(original.Reason, copied.Reason);
        Assert.Same(original, copied.InnerException);
        foreach (var property in identifiers)
        {
            if (property.Name == nameof(ShaderValidationException.RepairSourcePaths))
                Assert.Equal(original.RepairSourcePaths, copied.RepairSourcePaths);
            else Assert.Equal(property.GetValue(original), property.GetValue(copied));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsupported_real_emitted_helper_keeps_its_reason_through_cross_compile(bool pair)
    {
        string source = pair ? PreciseMathShaders.Vertex : PreciseMathShaders.Compute;
        GpuShaderStages stage = pair ? GpuShaderStages.Vertex : GpuShaderStages.Compute;
        byte[] module = SpirvFrontEnd.ToSpirv(source, stage, "precise wrapper", Precise);
        byte[] fragment = SpirvFrontEnd.ToSpirv(PreciseMathShaders.Fragment, GpuShaderStages.Fragment, "fragment");
        string emitted = pair
            ? SpirvCrossCompile.VertexFragmentToMsl(module, fragment, vertexOptions: Precise).VertexSource
            : SpirvCrossCompile.ComputeToMsl(module, options: Precise).ComputeSource;
        Assert.Contains("return fma(l, r, T(-0.0));", emitted);
        string unsupported = emitted.Replace("return fma(l, r, T(-0.0));", "return fma(l, r, T(1));", StringComparison.Ordinal);
        Assert.NotEqual(emitted, unsupported);
        ShaderValidationException error = Assert.Throws<ShaderValidationException>(() =>
        {
            if (pair) SpirvCrossCompile.PairToMslWithEmissionForTests(module, fragment, unsupported, "wrapper");
            else SpirvCrossCompile.ComputeToMslWithEmissionForTests(module, unsupported, "wrapper");
        });
        Assert.Equal(ShaderValidationRefusal.PreciseMslHelperUnsupported, error.Reason);
        Assert.Equal("spvFMul", error.Operation);
        Assert.Equal("wrapper", error.Label);
        Assert.Equal(stage, error.Stage);
        Assert.Equal("MSL", error.Target);
        Assert.Equal("spvFMul", Assert.IsType<ShaderValidationException>(error.InnerException).Operation);
    }
}
