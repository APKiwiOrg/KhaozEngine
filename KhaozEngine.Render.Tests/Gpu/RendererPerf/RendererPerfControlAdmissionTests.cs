using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfControlAdmissionTests
{
    [Fact]
    public void Unknown_storage_limit_refuses_before_control_resources_or_frames()
    {
        using var device = new RendererPerfConstructionFixture();
        device.Features = device.Features with { MaxStorageBufferRangeBytes = 0 };
        device.ColdAction = stage =>
        {
            if (stage == "framebuffer") Assert.Fail("Device compatibility must be admitted before control resources are created.");
        };
        var facts = RendererPerfControls.Definition(7);

        var error = Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfControls.Create(device, new(7, "1", facts.WorkSha256, 32, 64)));

        Assert.Contains("Control 7", error.Message, StringComparison.Ordinal);
        Assert.Contains("268435456", error.Message, StringComparison.Ordinal);
        Assert.Contains("device limit 0 bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.TextureAttempts);
        Assert.Equal(0, device.Submissions);
    }

    [Theory]
    [InlineData(1, 0u)]
    [InlineData(2, 0u)]
    [InlineData(3, 0u)]
    [InlineData(4, 4u)]
    [InlineData(5, 4u)]
    [InlineData(6, 4u)]
    [InlineData(7, 268435456u)]
    [InlineData(8, 33554432u)]
    [InlineData(9, 2097152u)]
    [InlineData(10, 262144u)]
    [InlineData(11, 0u)]
    [InlineData(12, 0u)]
    public void Every_control_admits_each_whole_binding_at_its_exact_limit(int id, uint requiredBytes)
    {
        using var device = new RendererPerfConstructionFixture();
        var facts = RendererPerfControls.Definition(id);
        var control = new RendererPerfControl(id, "1", facts.WorkSha256, 32, 64);
        device.Features = device.Features with { MaxStorageBufferRangeBytes = requiredBytes };
        var reachedFactory = new InvalidOperationException("Admission reached resource construction.");
        device.ColdAction = stage => { if (stage == "framebuffer") throw reachedFactory; };

        var accepted = Assert.Throws<GpuDiagnosticFailureException>(() => RendererPerfControls.Create(device, control));
        Assert.Same(reachedFactory, Assert.Single(accepted.Failures).Error);

        if (requiredBytes != 0)
        {
            device.TextureAttempts = 0;
            device.Features = device.Features with { MaxStorageBufferRangeBytes = requiredBytes - 1 };
            var refused = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfControls.Create(device, control));
            Assert.Contains("Control " + id, refused.Message, StringComparison.Ordinal);
            Assert.Contains(requiredBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
            Assert.Contains((requiredBytes - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, device.TextureAttempts);
        }
        Assert.Equal(0, device.Submissions);
    }

    [Theory]
    [InlineData("lavapipe", false)]
    [InlineData("WARP", false)]
    [InlineData("T4", true)]
    [InlineData("vulkan-256-mib", true)]
    public void Control_seven_uses_the_selected_native_capability(string target, bool compatible)
    {
        using var device = new RendererPerfConstructionFixture();
        var facts = RendererPerfControls.Definition(7);
        var control = new RendererPerfControl(7, "1", facts.WorkSha256, 32, 64);
        var reachedFactory = new InvalidOperationException("Admission reached resource construction.");
        device.ColdAction = stage => { if (stage == "framebuffer") throw reachedFactory; };
        device.Features = target switch
        {
            "lavapipe" => VulkanCapabilityRead.Assemble(target, false, true, GpuSampleCounts.One, 4,
                maxStorageBufferRangeBytes: 134217728),
            "vulkan-256-mib" => VulkanCapabilityRead.Assemble(target, false, true, GpuSampleCounts.One, 4,
                maxStorageBufferRangeBytes: 268435456),
            "WARP" => D3D11CapabilityRead.Assemble(target, 1, true, true, bufferLimits: new(0xb000, 0)),
            "T4" => D3D11CapabilityRead.Assemble(target, 1, true, true, bufferLimits: new(0xb000, 17179869184)),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        if (compatible)
        {
            var accepted = Assert.Throws<GpuDiagnosticFailureException>(() => RendererPerfControls.Create(device, control));
            Assert.Same(reachedFactory, Assert.Single(accepted.Failures).Error);
        }
        else
        {
            Assert.Throws<RendererPerfProtocolException>(() => RendererPerfControls.Create(device, control));
            Assert.Equal(0, device.TextureAttempts);
        }
        Assert.Equal(0, device.Submissions);
    }

    [Fact]
    public void Missing_compute_support_refuses_before_control_construction()
    {
        using var device = new RendererPerfConstructionFixture();
        device.Features = new(false, true) { MaxStorageBufferRangeBytes = uint.MaxValue };
        device.ColdAction = stage => { if (stage == "framebuffer") Assert.Fail("Compute support must be admitted first."); };
        var facts = RendererPerfControls.Definition(4);

        var error = Assert.Throws<RendererPerfProtocolException>(() =>
            RendererPerfControls.Create(device, new(4, "1", facts.WorkSha256, 32, 64)));

        Assert.Contains("Control 4", error.Message, StringComparison.Ordinal);
        Assert.Contains("compute", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, device.TextureAttempts);
        Assert.Equal(0, device.Submissions);
    }

    [Fact]
    public void Incompatible_control_refuses_the_unit_before_the_first_scene_session()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        rig.Production.Features = rig.Production.Features with { MaxStorageBufferRangeBytes = 134217728 };
        rig.SessionCreated = _ => throw new InvalidOperationException("Storage compatibility must precede the first scene session.");

        var outcome = rig.Run();

        Assert.Equal(0, rig.CreatedSessions);
        Assert.Equal(0, rig.Frames);
        Assert.Equal(0, rig.Production.TextureAttempts);
        Assert.Contains(outcome.Failures, failure => failure.Error is RendererPerfProtocolException
            && failure.Error.Message.Contains("Control 7", StringComparison.Ordinal)
            && failure.Error.Message.Contains("134217728", StringComparison.Ordinal));
        Assert.NotNull(outcome.EarlyFailure);
    }
}
