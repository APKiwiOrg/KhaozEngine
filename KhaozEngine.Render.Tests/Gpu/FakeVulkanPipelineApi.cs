using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// AN <see cref="IVulkanPipelineApi"/> WITH NO DEVICE BEHIND IT: every creation is recorded and nothing else
    /// happens, so the vertex input derivation, the blend attachment count, the rendering create-info's formats,
    /// the dynamic state list and the whole disk-cache lifecycle run under a plain <c>[Fact]</c> on a machine with
    /// no Vulkan loader. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para>IT KEEPS THE SPECS RATHER THAN COUNTING THEM, for the reason
    /// <see cref="FakeVulkanRenderApi"/> keeps its arguments: "one pipeline was created" is a count, while "its
    /// attribute at location 4 reads slot 1 at offset 12" is the claim that actually matters, and a counter cannot
    /// tell a correct pipeline from one whose instance stream is read as vertex data.</para>
    ///
    /// <para>THE CACHE ARM IS PROGRAMMABLE, because the best-effort path is the point of decision V-S7 and every
    /// arm of it has to be reachable: <see cref="FailCacheCreation"/> is the driver that refuses a seeded cache
    /// (which is what makes the retry-with-no-seed observable), and <see cref="CacheData"/> is what
    /// <c>vkGetPipelineCacheData</c> hands back at teardown.</para>
    /// </summary>
    internal sealed class FakeVulkanPipelineApi : IVulkanPipelineApi
    {
        readonly List<VulkanGraphicsPipelineSpec> _graphics = new();
        readonly List<VulkanComputePipelineSpec> _compute = new();
        readonly List<byte[]> _cacheSeeds = new();
        readonly List<ulong> _destroyedPipelines = new();
        readonly List<ulong> _destroyedCaches = new();

        ulong _nextPipeline = 0x7000;
        ulong _nextCache = 0x9000;

        /// <summary>Every graphics pipeline created, in order.</summary>
        internal IReadOnlyList<VulkanGraphicsPipelineSpec> Graphics => _graphics;

        /// <summary>Every compute pipeline created, in order.</summary>
        internal IReadOnlyList<VulkanComputePipelineSpec> Compute => _compute;

        /// <summary>The <c>VkPipelineCache</c> handle each creation was compiled through, in order. Zero means
        /// the pipeline was compiled with no cache at all.</summary>
        internal List<ulong> CachesUsed { get; } = new();

        /// <summary>Every seed handed to <c>vkCreatePipelineCache</c>, in order. An empty entry is a cold
        /// creation, which is also what the retry after a refused seed looks like.</summary>
        internal IReadOnlyList<byte[]> CacheSeeds => _cacheSeeds;

        /// <summary>Every handle handed to <c>vkDestroyPipeline</c>, in destroy order.</summary>
        internal IReadOnlyList<ulong> DestroyedPipelines => _destroyedPipelines;

        /// <summary>Every handle handed to <c>vkDestroyPipelineCache</c>, in destroy order.</summary>
        internal IReadOnlyList<ulong> DestroyedCaches => _destroyedCaches;

        /// <summary>What <see cref="ReadCacheData"/> answers. Empty is the driver with nothing to persist.
        /// </summary>
        internal byte[] CacheData { get; set; } = [];

        /// <summary>When true, a creation carrying a NON-EMPTY seed answers 0, which is the driver that refuses a
        /// blob this backend's own header check accepted. A cold creation still succeeds, which is what makes the
        /// retry visible.</summary>
        internal bool FailCacheCreation { get; set; }

        /// <inheritdoc/>
        public ulong CreateCache(ReadOnlySpan<byte> seed)
        {
            _cacheSeeds.Add(seed.ToArray());

            if (FailCacheCreation && !seed.IsEmpty) return 0;

            return _nextCache++;
        }

        /// <inheritdoc/>
        public byte[] ReadCacheData(ulong cache) => cache == 0 ? [] : CacheData;

        /// <inheritdoc/>
        public void DestroyCache(ulong cache) => _destroyedCaches.Add(cache);

        /// <inheritdoc/>
        public ulong CreateGraphicsPipeline(ulong cache, VulkanGraphicsPipelineSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);

            _graphics.Add(spec);
            CachesUsed.Add(cache);
            return _nextPipeline++;
        }

        /// <inheritdoc/>
        public ulong CreateComputePipeline(ulong cache, in VulkanComputePipelineSpec spec)
        {
            _compute.Add(spec);
            CachesUsed.Add(cache);
            return _nextPipeline++;
        }

        /// <inheritdoc/>
        public void DestroyPipeline(ulong pipeline) => _destroyedPipelines.Add(pipeline);
    }
}
