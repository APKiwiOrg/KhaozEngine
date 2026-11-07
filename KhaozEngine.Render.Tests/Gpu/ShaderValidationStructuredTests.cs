using System;
using System.Buffers.Binary;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class ShaderValidationStructuredTests
{
    [Fact]
    public void EngineCallersAlwaysSupplyAReason()
    {
        var constructors = new[]
        {
            typeof(ShaderValidationException).GetConstructor([typeof(string)])!,
            typeof(ShaderValidationException).GetConstructor([typeof(string), typeof(Exception)])!,
        };
        Assert.Empty(RefusalConstructorFence.EngineCallers(
            [typeof(ShaderValidation).Assembly, typeof(KhaozEngineD3D11).Assembly,
                typeof(KhaozEngineMetal).Assembly, typeof(KhaozEngineVulkan).Assembly], constructors));
        Assert.Null(new ShaderValidationException("consumer").Reason);
        var inner = new InvalidOperationException();
        var consumer = new ShaderValidationException("consumer", inner);
        Assert.Null(consumer.Reason);
        Assert.Same(inner, consumer.InnerException);
    }

    [Fact]
    public void BufferKindFallbackCarriesBothKindsAndTheSlot()
    {
        var reflection = new ShaderReflection([], [new(new GpuResourceLayoutElement("u", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute))]);
        var result = new CrossCompiledCompute("kernel void main0(constant float& u [[buffer(0)]]) {}", reflection, []);
        ShaderValidation.CheckMslBufferSlots(result, "fallback");
        var error = Assert.Throws<ShaderValidationException>(() => ShaderValidation.CheckMslBufferSlots(
            result with { ComputeSource = "kernel void main0(device float& u [[buffer(0)]]) {}" }, "fallback"));
        Assert.Equal(ShaderValidationRefusal.MetalBufferKindOrderMismatch, error.Reason);
        Assert.Equal("fallback", error.Label);
        Assert.Equal(0, error.Slot);
        Assert.Equal("uniform", error.DeclaredBufferKind);
        Assert.Equal("storage", error.EmittedKind);
    }

    [Fact]
    public void BindingOrderCarriesTheEmittedAndDeclaredLocations()
    {
        uint[] words = [0x07230203, 0x00010000, 0, 10, 0,
            (4u << 16) | 71u, 7, 34, 0, (4u << 16) | 71u, 7, 33, 0,
            (4u << 16) | 71u, 8, 34, 0, (4u << 16) | 71u, 8, 33, 1];
        var bytes = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        Assert.NotNull(MslBindingOrder.CheckStage(bytes,
            "kernel void main0(device float& _7 [[buffer(0)]], device float& _8 [[buffer(1)]]) {}", "compute", "order"));
        var error = Assert.Throws<ShaderValidationException>(() => MslBindingOrder.CheckStage(bytes,
            "kernel void main0(device float& _8 [[buffer(0)]], device float& _7 [[buffer(1)]]) {}", "compute", "order"));
        Assert.Equal(ShaderValidationRefusal.MetalBindingOrderMismatch, error.Reason);
        Assert.Equal("order", error.Label);
        Assert.Equal(GpuShaderStages.Compute, error.Stage);
        Assert.Equal("buffer", error.Space);
        Assert.Equal(0, error.ArgumentIndex);
        Assert.Equal(0u, error.DeclaredSet);
        Assert.Equal(0u, error.DeclaredBinding);
        Assert.Equal(0u, error.EmittedSet);
        Assert.Equal(1u, error.EmittedBinding);
        Assert.Equal(1, error.ExpectedIndex);
    }

    [Fact]
    public void CrossCompileLengthRefusalRetainsTheInnerModuleFailure()
    {
        var error = Assert.Throws<ShaderValidationException>(() => SpirvCrossCompile.ComputeToHlsl(new byte[3], "unaligned"));
        Assert.Equal(ShaderValidationRefusal.CrossCompileFailed, error.Reason);
        Assert.Equal("unaligned", error.Label);
        Assert.Equal("HLSL", error.Target);
        Assert.Equal(GpuShaderStages.Compute, error.Stage);
        var module = Assert.IsType<ShaderValidationException>(error.InnerException);
        Assert.Equal(ShaderValidationRefusal.SpirvLengthInvalid, module.Reason);
        Assert.Equal("unaligned", module.Label);
        Assert.Equal(3, module.ByteLength);
        Assert.Equal(4, module.WordBytes);
        Assert.Null(module.MinimumHeaderBytes);
    }
}
