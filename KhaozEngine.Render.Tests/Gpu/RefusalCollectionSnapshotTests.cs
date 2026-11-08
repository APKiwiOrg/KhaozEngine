using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class RefusalCollectionSnapshotTests
{
    // Task 12f fix round 1 requires exception-owned snapshots, including consumer init values.
    [Fact]
    public void VulkanCandidateIdentifiersCannotBeChangedThroughTheirSourceOrExposedLists()
    {
        string[] names = ["device A", "device B"];
        string?[] rejections = ["feature absent", null];
        var refusal = new VulkanBackendCreationStateException(VulkanBackendCreationRefusal.EligibleDeviceMissing, "refused")
        {
            CandidateNames = names,
            CandidateRejections = rejections,
        };

        names[0] = "changed";
        rejections[0] = "changed";
        Assert.Equal(new[] { "device A", "device B" }, refusal.CandidateNames);
        Assert.Equal(new string?[] { "feature absent", null }, refusal.CandidateRejections);
        Assert.Throws<InvalidCastException>(() => (string[])refusal.CandidateNames!);
        Assert.Throws<InvalidCastException>(() => (string?[])refusal.CandidateRejections!);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)refusal.CandidateNames!)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)refusal.CandidateNames!).SyncRoot);
        Assert.Throws<NotSupportedException>(() => ((IList<string?>)refusal.CandidateRejections!)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)refusal.CandidateRejections!).SyncRoot);
        var absent = new VulkanBackendCreationStateException(VulkanBackendCreationRefusal.PhysicalDevicesMissing, "refused")
        {
            CandidateNames = null,
            CandidateRejections = null,
        };
        Assert.Null(absent.CandidateNames);
        Assert.Null(absent.CandidateRejections);
    }

    [Fact]
    public void ShaderRepairPathsCannotBeChangedThroughTheirSourceOrExposedList()
    {
        string[] paths = ["ShaderSources.Shadow.cs", "ShaderSources.Terrain.cs"];
        var refusal = new ShaderValidationException(ShaderValidationRefusal.D3D11UserSemanticHole, "refused")
        {
            RepairSourcePaths = paths,
        };

        paths[0] = "changed";
        Assert.Equal(new[] { "ShaderSources.Shadow.cs", "ShaderSources.Terrain.cs" }, refusal.RepairSourcePaths);
        Assert.Throws<InvalidCastException>(() => (string[])refusal.RepairSourcePaths!);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)refusal.RepairSourcePaths!)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)refusal.RepairSourcePaths!).SyncRoot);
        Assert.Null(new ShaderValidationException("consumer") { RepairSourcePaths = null }.RepairSourcePaths);
    }
}
