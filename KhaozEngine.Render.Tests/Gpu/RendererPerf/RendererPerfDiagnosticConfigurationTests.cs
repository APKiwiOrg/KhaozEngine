using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed partial class RendererPerfDiagnosticConfigurationTests
{
    [Fact]
    public void Explicit_instances_do_not_read_or_mutate_process_environment()
    {
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        string? Read(string key) { reads[key] = reads.GetValueOrDefault(key) + 1; return null; }
        var production = VulkanHeadlessDiagnosticSettings.Resolve(new(false, false), Read);
        int count = reads.Count;
        var replay = (VulkanHeadlessDiagnosticSettings)production.WithDiagnostics(new(true, false)).WithAdapter("uuid:0011");
        Assert.False(production.Diagnostics.CompilerMetadata);
        Assert.True(replay.Diagnostics.CompilerMetadata);
        Assert.False(replay.Diagnostics.RuntimeInvocations);
        Assert.Equal(production.FramesInFlight, replay.FramesInFlight);
        Assert.Equal(production.SpirvCache, replay.SpirvCache);
        Assert.Equal(count, reads.Count);
        Assert.All(reads.Values, n => Assert.Equal(1, n));
    }

    [Theory]
    [InlineData("KE_D3D11_PIPELINE_DIAGNOSTICS")]
    [InlineData("KE_D3D11_INVOCATION_DIAGNOSTICS")]
    public void Timed_D3D_excludes_reflection_and_invocations_independently(string enabled)
    {
        Assert.Throws<InvalidOperationException>(() => D3D11HeadlessDiagnosticSettings.Resolve(new(false, false),
            key => key == enabled ? "1" : null));
        var settings = D3D11HeadlessDiagnosticSettings.Resolve(new(false, false), _ => null);
        Assert.False(settings.Diagnostics.CompilerMetadata);
        Assert.False(settings.Diagnostics.RuntimeInvocations);
        Assert.False(settings.CompilerOptions(false).Reflection);
        Assert.False(settings.CompilerOptions(false).Invocations);
    }
    [Fact]
    public void Real_providers_forward_frozen_instances_without_environment_rereads()
    {
        int reads = 0, created = 0;
        string? Read(string _) { reads++; return null; }
        using var native = new FakeGpuDevice(GpuBackendKind.VulkanNative);
        var provider = new VulkanBackendProvider(Read, settings =>
        {
            created++; Assert.Equal(created == 2, settings.Diagnostics.CompilerMetadata);
            Assert.False(settings.Diagnostics.RuntimeInvocations);
            return new(native, null, null);
        });
        using var scope = new BackendProviderScope(GpuBackendKind.VulkanNative, provider);
        var production = GpuDeviceContext.ResolveHeadlessDiagnosticSettings(GpuBackendKind.VulkanNative, new(false, false));
        int frozenReads = reads;
        var first = GpuDeviceContext.CreateHeadlessAttempt(GpuBackendKind.VulkanNative, production);
        Assert.Empty(first.Failures); first.Context!.Dispose();
        var second = GpuDeviceContext.CreateHeadlessAttempt(GpuBackendKind.VulkanNative,
            production.WithDiagnostics(new(true, false)).WithAdapter("synthetic native-operation seam"));
        Assert.Empty(second.Failures); second.Context!.Dispose();
        Assert.Equal(frozenReads, reads); Assert.Equal(2, created);
    }

    [Fact]
    public void Actual_D3D_compiler_uses_frozen_flags_and_excludes_reflection_without_invocation_coupling()
    {
        var settings = D3D11HeadlessDiagnosticSettings.Resolve(new(false, false), _ => null);
        var native = new CompilerApi(); var reflection = new ReflectionApi();
        var compiler = new D3D11ShaderCompiler(new DeviceLiveness(), native, null, settings.ShaderFlags,
            settings.CompilerOptions(false), reflection);
        using var shader = (D3D11ComputeShader)compiler.CreateComputeShader("void main() {}");
        Assert.Equal(settings.ShaderFlags, native.Flags);
        Assert.False(shader.DiagnosticEvidence!.ReflectionRequested);
        Assert.Equal(0, reflection.Reads);
        var replay = (D3D11HeadlessDiagnosticSettings)settings.WithDiagnostics(new(true, false));
        var diagnostic = new D3D11ShaderCompiler(new DeviceLiveness(), native, null, replay.ShaderFlags,
            replay.CompilerOptions(false), reflection);
        using var second = (D3D11ComputeShader)diagnostic.CreateComputeShader("void main() {}");
        Assert.Equal(1, reflection.Reads);
        Assert.True(second.DiagnosticEvidence!.ReflectionRequested);
        Assert.False(replay.CompilerOptions(false).Invocations);
        Assert.Equal(shader.DiagnosticEvidence.CodeGeneration, second.DiagnosticEvidence.CodeGeneration);
        Assert.Equal(shader.DiagnosticEvidence.Artifact, second.DiagnosticEvidence.Artifact);
    }

    [Fact]
    public void Vulkan_supported_capture_omission_is_invalid_extension_absence_is_unsupported()
    {
        var witness = RendererPerfCompilerFixture.Identity(new string('a', 64));
        var identity = new VulkanPipelineIdentity(new string('a', 64), new string('b', 64), new string('c', 64),
            new string('d', 64), "synthetic native operations")
        { Stages = witness.Stages, CodeGeneration = witness.CodeGenerationSha256 };
        var request = RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.VulkanNative, 1048576)!.Request;
        var omitted = new VulkanPipelineDiagnostics(true, true, true, null, "synthetic native configuration")
            .Read(1, 0, identity, request, _ => { }, default);
        Assert.Equal(GpuDiagnosticStatus.Invalid, omitted.Report.Status); Assert.False(omitted.Complete);
        var absent = new VulkanPipelineDiagnostics(true, false, false, null, "synthetic native configuration")
            .Read(1, 0, identity, request, _ => { }, default);
        Assert.Equal(GpuDiagnosticStatus.Unsupported, absent.Report.Status); Assert.True(absent.Complete);
        Assert.False(absent.Counts.EnumerationRequired);
    }

    [Fact]
    public void Metal_keeps_its_optional_metadata_route_without_claiming_a_native_artifact_or_invocations()
    {
        var settings = MetalHeadlessDiagnosticSettings.Resolve(new(false, false), _ => null, default);
        var replay = (MetalHeadlessDiagnosticSettings)settings.WithDiagnostics(new(true, false));
        Assert.Equal(settings.Device, replay.Device); Assert.Equal(settings.Validation, replay.Validation);
        Assert.Equal(settings.MslCache, replay.MslCache); Assert.Equal(settings.FramesInFlight, replay.FramesInFlight);
        Assert.Throws<NotSupportedException>(() => settings.WithDiagnostics(new(false, true)));
    }

    sealed class CompilerApi : ID3D11ShaderCompilerApi
    {
        internal uint Flags;
        public D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache)
        { Flags = flags; return new([1], [2]); }
        public D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache)
        { Flags = flags; return new([1], 1, 1, 1); }
        public object CreateShader(byte[] dxbc, D3D11ShaderStage stage) => new ShaderHandle();
        sealed class ShaderHandle : IDisposable { public void Dispose() { } }
    }
    sealed class ReflectionApi : ID3D11ShaderReflectionApi
    {
        internal int Reads;
        public D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)
        { Reads++; return new("synthetic compiler observation", []); }
    }

}
