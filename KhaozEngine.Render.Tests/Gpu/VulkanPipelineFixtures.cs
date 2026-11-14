using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu;

// Pipelines on the fake native seam, for command-path tests that do not compile shaders.
internal static class VulkanPipelineFixtures
{
    internal static void BindBoth(VulkanCommandList list, VulkanResourceFixture fixture, List<IDisposable> owned)
    {
        // Reuse the owned wrappers when a test starts another recording on the same rig.
        var graphics = owned.Find(resource => resource is VulkanGraphicsPipeline) as VulkanGraphicsPipeline
            ?? Graphics(fixture);
        var compute = owned.Find(resource => resource is VulkanComputePipeline) as VulkanComputePipeline
            ?? Compute(fixture);
        if (!owned.Contains(graphics)) owned.Add(graphics);
        if (!owned.Contains(compute)) owned.Add(compute);
        list.SetPipeline(graphics);
        list.SetComputePipeline(compute);
    }

    internal static VulkanGraphicsPipeline Graphics(VulkanResourceFixture fixture)
        => new(new VulkanPipelineOwner(fixture.PipelineApi, fixture.Timeline, fixture.Retired),
            10001, fixture.Descriptors.PipelineLayouts.GetOrCreate([], 0), []);

    internal static VulkanComputePipeline Compute(VulkanResourceFixture fixture)
        => new(new VulkanPipelineOwner(fixture.PipelineApi, fixture.Timeline, fixture.Retired),
            10002, fixture.Descriptors.PipelineLayouts.GetOrCreate([], 0), []);
}
