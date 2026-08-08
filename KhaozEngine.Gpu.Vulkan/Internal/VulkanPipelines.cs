using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT THE PIPELINE SUBSYSTEM NEEDS FROM ITS DEVICE, in one object, for the same reason
    /// <see cref="VulkanDescriptorOwner"/> exists: three things that must always travel together and must always
    /// be the SAME three.
    /// <para>
    /// It carries no memory allocator, because a <c>VkPipeline</c>'s storage is the driver's own and is not
    /// visible through <c>vkAllocateMemory</c> at all.
    /// </para>
    /// </summary>
    /// <param name="Api">The six native pipeline calls.</param>
    /// <param name="Timeline">The device's ONE completion timeline, whose current value a deferred destroy is
    /// recorded at (V-F9).</param>
    /// <param name="Retired">The device's ONE deferred-disposal list.</param>
    internal sealed record VulkanPipelineOwner(
        IVulkanPipelineApi Api,
        VulkanTimeline Timeline,
        VulkanRetireList Retired)
    {
        /// <summary>
        /// HOLD ONE TERMINAL DESTROY behind the timeline (V-F9): record the value the device has handed out most
        /// recently, and run <paramref name="release"/> once the GPU has passed it. A pipeline destroyed under a
        /// submission that bound it is undefined behaviour, and terminal because the destroy retires nothing
        /// further, so the retirement depth stays at the generation the device's two teardown drains cover.
        /// </summary>
        internal void RetireTerminal(Action release)
        {
            ArgumentNullException.ThrowIfNull(release);
            Retired.Retire(Timeline.LastAllocated, release);
        }
    }

    /// <summary>
    /// THE DEVICE'S WHOLE PIPELINE SUBSYSTEM in one object: the native seam, the <c>VkPipelineCache</c> every
    /// creation is compiled through (V-S7), and the two creations themselves. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>IT IS ONE OBJECT FOR THE REASON <see cref="VulkanDescriptors"/> IS.</b> A device with two pipeline
    /// caches would compile the same stage twice and persist whichever blob was written last, and a device with
    /// two seams would destroy pipelines through a line the creation never used.</para>
    ///
    /// <para><b>IT HOLDS THE PIPELINE-LAYOUT CACHE RATHER THAN OWNING ONE</b>, because a pipeline layout is a
    /// DESCRIPTOR object (<see cref="VulkanDescriptors.PipelineLayouts"/>) whose content dedup is what makes row
    /// 11's compatibility test a pointer compare. Creating a second cache here would hand out two handles for one
    /// content and silently break that. This is the first caller of it, and of the dynamic-uniform limit check
    /// that lives with it, which row 10 landed one row early for exactly this moment.</para>
    ///
    /// <para><b>AND LIKE THE DESCRIPTOR SUBSYSTEM IT IS UNREACHABLE FROM THE RECORDING TYPE.</b> Creating a
    /// pipeline is a shader compile, so a recorder able to reach one could compile inside a frame. The device
    /// holds this and hands it to <see cref="VulkanResourceFactory"/>, which is already on the unreachability
    /// test's forbidden list, and to nothing else. A command list gets <see cref="IVulkanPipelineBinder"/>
    /// instead, which can bind a pipeline that exists and make none.</para>
    /// </summary>
    internal sealed class VulkanPipelines
    {
        readonly VulkanPipelineOwner _owner;
        readonly VulkanPipelineLayoutCache _layouts;

        int _graphics;
        int _compute;

        /// <param name="owner">The device's pipeline seam, timeline and retire list.</param>
        /// <param name="layouts">The device's ONE content-deduplicated pipeline-layout cache, which lives with
        /// the descriptors because a <c>VkPipelineLayout</c> is made of set layouts.</param>
        /// <param name="file">Where the persisted cache blob lives, or null for no disk cache. The live
        /// <c>VkPipelineCache</c> is created either way.</param>
        internal VulkanPipelines(VulkanPipelineOwner owner, VulkanPipelineLayoutCache layouts,
            VulkanPipelineCacheFile? file)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(layouts);

            _owner = owner;
            _layouts = layouts;
            Cache = new VulkanPipelineCache(owner.Api, file);
        }

        /// <summary>The <c>VkPipelineCache</c> every creation here is compiled through.</summary>
        internal VulkanPipelineCache Cache { get; }

        /// <summary>How many graphics pipelines have been created.</summary>
        internal int GraphicsPipelineCount => _graphics;

        /// <summary>How many compute pipelines have been created.</summary>
        internal int ComputePipelineCount => _compute;

        /// <summary>
        /// Create a graphics pipeline: resolve the shader set and the layouts, take the SHARED
        /// <c>VkPipelineLayout</c> (which is where the dynamic-uniform limit is checked, 8.3's third defence),
        /// build the spec and compile it through the cache.
        /// </summary>
        /// <exception cref="ArgumentException">The shader set or a resource layout came from another backend, or
        /// a vertex layout declares an instance step rate this backend cannot express.</exception>
        /// <exception cref="NotSupportedException">The layouts spend more dynamic uniform descriptors between them
        /// than the device allows.</exception>
        internal IGpuPipeline CreateGraphics(in GpuPipelineDescription description)
        {
            const string what = "a native Vulkan graphics pipeline";

            VulkanShaderSet shaders = VulkanShaderSet.Require(description.ShaderSet, what);
            VulkanResourceLayout[] layouts = RequireLayouts(description.ResourceLayouts, what);

            ulong pipelineLayout = _layouts.GetOrCreate(layouts);
            VulkanGraphicsPipelineSpec spec = VulkanGraphicsPipelineSpec.For(
                description, pipelineLayout, shaders.VertexModule, shaders.FragmentModule);

            ulong handle = _owner.Api.CreateGraphicsPipeline(Cache.Handle, spec);
            _graphics++;

            return new VulkanGraphicsPipeline(_owner, handle, pipelineLayout, SetLayoutsOf(layouts));
        }

        /// <summary>Create a compute pipeline: the same resolution over one module and no graphics state at all.
        /// </summary>
        /// <exception cref="ArgumentException">The compute shader or a resource layout came from another backend.
        /// </exception>
        internal IGpuComputePipeline CreateCompute(in GpuComputePipelineDescription description)
        {
            const string what = "a native Vulkan compute pipeline";

            VulkanComputeShader shader = VulkanComputeShader.Require(description.Shader, what);
            VulkanResourceLayout[] layouts = RequireLayouts(description.ResourceLayouts, what);

            ulong pipelineLayout = _layouts.GetOrCreate(layouts);
            VulkanComputePipelineSpec spec = VulkanComputePipelineSpec.For(pipelineLayout, shader.Module);

            ulong handle = _owner.Api.CreateComputePipeline(Cache.Handle, spec);
            _compute++;

            return new VulkanComputePipeline(_owner, handle, pipelineLayout, SetLayoutsOf(layouts));
        }

        /// <summary>
        /// Write the cache back to disk and destroy it. Called ONCE, from the device's teardown window, while the
        /// device is still alive, and best effort at every step. It destroys no PIPELINE: each one is a real
        /// object whose <c>Dispose</c> retires its own destroy, and one a consumer never disposed goes with
        /// <c>vkDestroyDevice</c> like every other undisposed child.
        /// </summary>
        internal void DestroyAll()
        {
            Cache.Persist();
            Cache.Destroy();
        }

        /// <summary>The line a teardown diagnostic quotes.</summary>
        internal string Describe()
            => _graphics.ToString(CultureInfo.InvariantCulture) + " graphics and "
                + _compute.ToString(CultureInfo.InvariantCulture) + " compute pipelines, "
                + Cache.Describe();

        // The set-layout handles in slot order, as a FRESH array per pipeline. The pipeline-layout cache takes its
        // own over when it misses and discards it when it hits, so the array the bind records compare against has
        // to be this pipeline's rather than one shared with a cache key that may or may not have kept it.
        static ulong[] SetLayoutsOf(VulkanResourceLayout[] layouts)
        {
            var handles = new ulong[layouts.Length];
            for (int i = 0; i < layouts.Length; i++) handles[i] = layouts[i].SetLayout;

            return handles;
        }

        static VulkanResourceLayout[] RequireLayouts(IGpuResourceLayout[]? declared, string what)
        {
            IGpuResourceLayout[] source = declared ?? [];
            var layouts = new VulkanResourceLayout[source.Length];
            for (int i = 0; i < source.Length; i++) layouts[i] = VulkanResourceLayout.Require(source[i], what);

            return layouts;
        }
    }
}
