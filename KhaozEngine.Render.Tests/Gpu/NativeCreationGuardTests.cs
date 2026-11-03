using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class NativeCreationGuardTests
{
    [Fact]
    public void D3D11WindowAndRuntimePrerequisitesCarryTheirNativeIdentity()
    {
        var handle = Assert.Throws<D3D11BackendCreationArgumentException>(() => D3D11BackendCreationChecks.RequireWindowHandleForTests(IntPtr.Zero));
        Assert.Equal(D3D11BackendCreationRefusal.WindowHandleMissing, handle.Reason);
        Assert.Equal("hwnd", handle.ParamName);
        Assert.Equal(GpuWindowKind.Win32, handle.ActualWindowKind);
        D3D11BackendCreationChecks.RequireWindowHandleForTests(new IntPtr(1));
        var runtime = Assert.Throws<D3D11BackendCreationUnsupportedException>(() => D3D11BackendCreationChecks.RefuseRuntimeContextForTests("ID3D11DeviceContext1"));
        Assert.Equal(D3D11BackendCreationRefusal.RuntimeContextUnavailable, runtime.Reason);
        Assert.Equal("ID3D11DeviceContext1", runtime.RequiredInterface);
        Assert.IsAssignableFrom<NotSupportedException>(runtime);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void D3D11NativeSuccessWithoutItsObjectsNamesTheMissingOutput(bool device, bool context)
    {
        var refusal = Assert.Throws<D3D11BackendCreationStateException>(() => D3D11BackendCreationChecks.RefuseNativeObjectsForTests(device, context));
        Assert.Equal(D3D11BackendCreationRefusal.NativeObjectsMissing, refusal.Reason);
        Assert.Equal(device, refusal.DeviceReturned);
        Assert.Equal(context, refusal.ContextReturned);
        Assert.IsAssignableFrom<InvalidOperationException>(refusal);
    }

    [Fact]
    public void MetalPostProbeCreationRefusalsCarryDeviceAndRequirement()
    {
        var selection = Assert.Throws<MetalBackendCreationStateException>(() => MetalBackendCreationChecks.RequireEligibleDeviceForTests(false, "device removed"));
        Assert.Equal(MetalBackendCreationRefusal.EligibleDeviceMissing, selection.Reason);
        Assert.Equal("device removed", selection.Detail);
        MetalBackendCreationChecks.RequireEligibleDeviceForTests(true, null);
        var queue = Assert.Throws<MetalBackendCreationStateException>(() => MetalBackendCreationChecks.RequireCommandQueueForTests(false, "test device"));
        Assert.Equal(MetalBackendCreationRefusal.CommandQueueUnavailable, queue.Reason);
        Assert.Equal("test device", queue.DeviceName);
        MetalBackendCreationChecks.RequireCommandQueueForTests(true, "test device");
    }

    [Fact]
    public void VulkanPostProbeRefusalsCarryThePinAndEmptyDeviceCount()
    {
        var pin = Assert.Throws<VulkanBackendCreationStateException>(() => VulkanBackendCreationChecks.RequirePinnedAdapterForTests(-1, "uuid-token"));
        Assert.Equal(VulkanBackendCreationRefusal.PinnedAdapterUnavailable, pin.Reason);
        Assert.Equal("uuid-token", pin.AdapterKey);
        Assert.IsAssignableFrom<InvalidOperationException>(pin);
        VulkanBackendCreationChecks.RequirePinnedAdapterForTests(0, "uuid-token");
        var count = Assert.Throws<VulkanBackendCreationStateException>(() => VulkanBackendCreationChecks.RequirePhysicalDevicesForTests(0));
        Assert.Equal(VulkanBackendCreationRefusal.PhysicalDevicesMissing, count.Reason);
        Assert.Equal(0u, count.PhysicalDeviceCount);
        VulkanBackendCreationChecks.RequirePhysicalDevicesForTests(1);
    }

    [Fact]
    public void VulkanEligibilityRefusalKeepsEveryCandidateAndItsIndependentReason()
    {
        VulkanPhysicalDeviceInfo[] candidates = [new("device A", VulkanPhysicalDeviceClass.Discrete, false, false, "feature absent"), new("device B", VulkanPhysicalDeviceClass.Integrated, false, false, null)];
        var refusal = Assert.Throws<VulkanBackendCreationStateException>(() => VulkanBackendCreationChecks.RequireEligibleDeviceForTests(false, candidates));
        Assert.Equal(VulkanBackendCreationRefusal.EligibleDeviceMissing, refusal.Reason);
        Assert.Equal(2, refusal.CandidateCount);
        Assert.Equal(new[] { "device A", "device B" }, refusal.CandidateNames);
        Assert.Equal(new string?[] { "feature absent", null }, refusal.CandidateRejections);
        VulkanBackendCreationChecks.RequireEligibleDeviceForTests(true, candidates);
    }
}
