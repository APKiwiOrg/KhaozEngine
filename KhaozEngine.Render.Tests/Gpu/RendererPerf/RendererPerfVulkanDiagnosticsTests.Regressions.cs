using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfVulkanDiagnosticsTests
{
    [Theory]
    [InlineData(8u, 1u, 4_294_967_296ul)]
    [InlineData(9u, 1u, 4_831_838_208ul)]
    [InlineData(8u, 2u, 5_368_709_120ul)]
    public void Native_image_payload_at_and_above_4_GiB_has_no_staging_ceiling(
        uint layers, uint mipLevels, ulong expectedBytes)
    {
        var fixture = new VulkanResourceFixture();
        using var memory = fixture.Memory;
        using IGpuTexture texture = fixture.Factory.CreateTexture(new GpuTextureDescription(
            8192, 8192, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled,
            mipLevels: mipLevels, arrayLayers: layers));

        GpuResourceMemorySnapshot snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(expectedBytes, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(expectedBytes, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(1ul, snapshot.LiveResourceCount.Value!.UInt64);
    }

    [Theory]
    [InlineData(8u, 1u)]
    [InlineData(9u, 1u)]
    [InlineData(8u, 2u)]
    public void Staging_payload_above_uint_max_remains_refused(uint layers, uint mipLevels)
    {
        var shape = new VulkanStagingShape(8192, 8192, mipLevels, layers, GpuPixelFormat.R16G16B16A16Float);
        Assert.Throws<ArgumentOutOfRangeException>(() => VulkanStagingLayout.TotalBytes(shape));
    }

    [Theory]
    [InlineData(uint.MaxValue, uint.MaxValue, 1u)]
    [InlineData(uint.MaxValue, 536_870_912u, 2u)]
    public void Texture_payload_arithmetic_refuses_64_bit_overflow(uint width, uint height, uint mipLevels)
    {
        var shape = new VulkanStagingShape(width, height, mipLevels, 1, GpuPixelFormat.R16G16B16A16Float);
        Assert.Throws<OverflowException>(() => VulkanStagingLayout.LayerPitch(shape));
    }

    [Fact]
    public void Texture_payload_layer_multiplication_refuses_64_bit_overflow()
    {
        var shape = new VulkanStagingShape(1_073_741_824, 1_073_741_824, 1, 3,
            GpuPixelFormat.R16G16B16A16Float);
        Assert.Throws<OverflowException>(() => VulkanStagingLayout.PayloadBytes(shape));
    }

    [Theory]
    [InlineData((int)VulkanValidationMode.On)]
    [InlineData((int)VulkanValidationMode.Sync)]
    public void Same_validation_request_with_and_without_layer_has_different_identity(int requestedMode)
    {
        var mode = (VulkanValidationMode)requestedMode;
        VulkanInstanceValidation enabled = VulkanInstanceValidation.Select(mode, [VulkanValidation.LayerName]);
        VulkanInstanceValidation absent = VulkanInstanceValidation.Select(mode, []);

        Assert.Equal(mode, enabled.RequestedMode);
        Assert.Equal(mode, absent.RequestedMode);
        Assert.Equal(VulkanValidation.LayerName, Assert.Single(enabled.EnabledLayers));
        Assert.Empty(absent.EnabledLayers);
        Assert.Empty(absent.EnabledFeatures);
        Assert.NotEqual(ValidationConfigurationIdentity(enabled), ValidationConfigurationIdentity(absent));
    }

    [Fact]
    public void Enabled_validation_features_change_configuration_identity()
    {
        VulkanInstanceValidation sync = VulkanInstanceValidation.Select(VulkanValidationMode.Sync,
            [VulkanValidation.LayerName]);
        Assert.Equal(ValidationFeatureEnableEXT.SynchronizationValidationExt, Assert.Single(sync.EnabledFeatures));
        VulkanInstanceValidation withoutFeature = sync with { EnabledFeatures = [] };

        Assert.NotEqual(ValidationConfigurationIdentity(sync), ValidationConfigurationIdentity(withoutFeature));
    }

    [Fact]
    public void Requested_validation_mode_remains_a_separate_identity_fact()
    {
        VulkanInstanceValidation on = VulkanInstanceValidation.Select(VulkanValidationMode.On, []);
        VulkanInstanceValidation strict = VulkanInstanceValidation.Select(VulkanValidationMode.Strict, []);

        Assert.Empty(on.EnabledLayers);
        Assert.Empty(strict.EnabledLayers);
        Assert.Empty(on.EnabledFeatures);
        Assert.Empty(strict.EnabledFeatures);
        Assert.NotEqual(ValidationConfigurationIdentity(on), ValidationConfigurationIdentity(strict));
    }

    static string ValidationConfigurationIdentity(VulkanInstanceValidation validation)
    {
        var diagnostics = new VulkanPipelineDiagnostics(false, false, false, null, validation.DiagnosticConfiguration);
        return diagnostics.Read(73, 0, Identity).Identity.DiagnosticConfigurationSha256.Sha256!;
    }
}
