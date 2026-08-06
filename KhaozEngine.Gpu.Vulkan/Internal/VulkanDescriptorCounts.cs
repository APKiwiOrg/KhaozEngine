using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// HOW MANY DESCRIPTORS OF EACH OF THE SEVEN COUNTED TYPES something needs or has left, and the arithmetic a
    /// pool's budget is walked with. Decision V-D3's accounting, as a value with structural equality, so
    /// "the budget came back to exactly what it was" is one <c>Assert.Equal</c> rather than seven.
    ///
    /// <para><b>ALL SEVEN FIELDS EXIST BECAUSE THE INCUMBENT'S FREE PATH RESTORES FIVE.</b>
    /// <c>VkDescriptorPoolManager.PoolInfo.Free</c> (verified in <c>v4.9.0</c>, lines 166 to 178, unchanged
    /// upstream) adds back <c>RemainingSets</c>, <c>UniformBufferCount</c>, <c>SampledImageCount</c>,
    /// <c>SamplerCount</c>, <c>StorageBufferCount</c> and <c>StorageImageCount</c>, and never touches
    /// <c>UniformBufferDynamicCount</c> or <c>StorageBufferDynamicCount</c>, both of which its
    /// <c>Allocate</c> DOES spend. An application that churns dynamic-offset resource sets therefore leaks pool
    /// budget until a fresh pool spawns, and it never stops, because every spawned pool leaks the same way.</para>
    ///
    /// <para><b>AND IT BINDS HARDER HERE THAN THERE.</b> Decision V-D4 makes EVERY uniform buffer in every layout
    /// a <see cref="VulkanDescriptorType.UniformBufferDynamic"/>, where the incumbent only makes the elements the
    /// engine declared dynamic. So the counter the incumbent forgets is the one this backend spends on almost
    /// every set it will ever allocate, and the map editor churns those on every document load. Taking and
    /// restoring are ONE pair of methods here (<see cref="Take"/> and <see cref="Restore"/>) over ONE value, which
    /// is the structural reason the same divergence cannot be written: there is no second list of field names to
    /// keep in step with the first.</para>
    ///
    /// <para><b>SATURATING RATHER THAN WRAPPING IS DELIBERATE.</b> <see cref="Add"/> saturates at
    /// <see cref="uint.MaxValue"/> instead of overflowing, because a budget that wrapped to nearly zero would
    /// present as an allocation failure on a pool with room, which is the least diagnosable shape this arithmetic
    /// has.</para>
    /// </summary>
    /// <param name="UniformBuffer">Non-dynamic uniform buffers.</param>
    /// <param name="UniformBufferDynamic">Dynamic uniform buffers, which is every uniform element (V-D4).</param>
    /// <param name="StorageBuffer">Storage buffers, which both structured kinds map to.</param>
    /// <param name="StorageBufferDynamic">Dynamic storage buffers. Counted, never produced.</param>
    /// <param name="SampledImage">Sampled images, separate from their samplers.</param>
    /// <param name="StorageImage">Storage images.</param>
    /// <param name="Sampler">Samplers, separate from their images.</param>
    internal readonly record struct VulkanDescriptorCounts(
        uint UniformBuffer,
        uint UniformBufferDynamic,
        uint StorageBuffer,
        uint StorageBufferDynamic,
        uint SampledImage,
        uint StorageImage,
        uint Sampler)
    {
        /// <summary>Every counted type, in one place, so a walk over the seven cannot miss one.</summary>
        internal static readonly VulkanDescriptorType[] CountedTypes =
        [
            VulkanDescriptorType.UniformBuffer,
            VulkanDescriptorType.UniformBufferDynamic,
            VulkanDescriptorType.StorageBuffer,
            VulkanDescriptorType.StorageBufferDynamic,
            VulkanDescriptorType.SampledImage,
            VulkanDescriptorType.StorageImage,
            VulkanDescriptorType.Sampler,
        ];

        /// <summary>How many descriptors of one type this value carries.</summary>
        internal uint CountOf(VulkanDescriptorType type) => type switch
        {
            VulkanDescriptorType.UniformBuffer => UniformBuffer,
            VulkanDescriptorType.UniformBufferDynamic => UniformBufferDynamic,
            VulkanDescriptorType.StorageBuffer => StorageBuffer,
            VulkanDescriptorType.StorageBufferDynamic => StorageBufferDynamic,
            VulkanDescriptorType.SampledImage => SampledImage,
            VulkanDescriptorType.StorageImage => StorageImage,
            VulkanDescriptorType.Sampler => Sampler,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type,
                "A native Vulkan descriptor type outside the seven this backend counts. Every type the layout "
                + "policy can produce is one of them, so this is a new enum member whose pool accounting was not "
                + "written."),
        };

        /// <summary>The same value with <paramref name="type"/>'s count raised by one, saturating.</summary>
        internal VulkanDescriptorCounts Incremented(VulkanDescriptorType type) => type switch
        {
            VulkanDescriptorType.UniformBuffer => this with { UniformBuffer = Bump(UniformBuffer) },
            VulkanDescriptorType.UniformBufferDynamic
                => this with { UniformBufferDynamic = Bump(UniformBufferDynamic) },
            VulkanDescriptorType.StorageBuffer => this with { StorageBuffer = Bump(StorageBuffer) },
            VulkanDescriptorType.StorageBufferDynamic
                => this with { StorageBufferDynamic = Bump(StorageBufferDynamic) },
            VulkanDescriptorType.SampledImage => this with { SampledImage = Bump(SampledImage) },
            VulkanDescriptorType.StorageImage => this with { StorageImage = Bump(StorageImage) },
            VulkanDescriptorType.Sampler => this with { Sampler = Bump(Sampler) },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type,
                "A native Vulkan descriptor type outside the seven this backend counts."),
        };

        /// <summary>What one layout's bindings cost, which is what a set allocated on it SPENDS and what its free
        /// RESTORES. One computation, used by both directions.</summary>
        internal static VulkanDescriptorCounts ForBindings(ReadOnlySpan<VulkanDescriptorBinding> bindings)
        {
            var counts = default(VulkanDescriptorCounts);
            for (int i = 0; i < bindings.Length; i++)
            {
                // descriptorCount is always 1 (8.1), and the loop reads it anyway rather than assuming, so a
                // future array binding costs what it really costs instead of one.
                for (uint n = 0; n < bindings[i].DescriptorCount; n++)
                {
                    counts = counts.Incremented(bindings[i].Type);
                }
            }

            return counts;
        }

        /// <summary>Whether nothing at all is needed, which is what a layout with no elements costs.</summary>
        internal bool IsEmpty
            => UniformBuffer == 0 && UniformBufferDynamic == 0 && StorageBuffer == 0 && StorageBufferDynamic == 0
                && SampledImage == 0 && StorageImage == 0 && Sampler == 0;

        /// <summary>The total across every type, for the diagnostics and for the pool sizing.</summary>
        internal ulong Total
            => (ulong)UniformBuffer + UniformBufferDynamic + StorageBuffer + StorageBufferDynamic + SampledImage
                + StorageImage + Sampler;

        /// <summary>Whether this budget can satisfy <paramref name="request"/> on EVERY type. The incumbent's own
        /// walk asks the same question and gets one term wrong: its <c>Allocate</c> compares
        /// <c>StorageBufferCount &gt;= counts.SamplerCount</c>, so a set with more storage buffers than samplers
        /// can be admitted to a pool that cannot hold it.</summary>
        internal bool Fits(in VulkanDescriptorCounts request)
            => UniformBuffer >= request.UniformBuffer
                && UniformBufferDynamic >= request.UniformBufferDynamic
                && StorageBuffer >= request.StorageBuffer
                && StorageBufferDynamic >= request.StorageBufferDynamic
                && SampledImage >= request.SampledImage
                && StorageImage >= request.StorageImage
                && Sampler >= request.Sampler;

        /// <summary>
        /// SPEND <paramref name="request"/> OUT OF THIS BUDGET, on all seven types. Refuses rather than
        /// underflowing, because a wrapped budget reads as nearly unlimited and the failure surfaces as a driver
        /// allocation error much later.
        /// </summary>
        internal VulkanDescriptorCounts Take(in VulkanDescriptorCounts request)
        {
            if (!Fits(request))
            {
                throw new InvalidOperationException(
                    "A native Vulkan descriptor pool budget of " + Describe()
                    + " was asked to spend " + request.Describe()
                    + ", which it cannot cover. The caller is meant to have asked Fits first.");
            }

            return new VulkanDescriptorCounts(
                UniformBuffer - request.UniformBuffer,
                UniformBufferDynamic - request.UniformBufferDynamic,
                StorageBuffer - request.StorageBuffer,
                StorageBufferDynamic - request.StorageBufferDynamic,
                SampledImage - request.SampledImage,
                StorageImage - request.StorageImage,
                Sampler - request.Sampler);
        }

        /// <summary>
        /// GIVE <paramref name="request"/> BACK, on all seven types. This is the method the incumbent writes with
        /// five of its seven fields, and the reason it is a single expression here rather than seven statements.
        /// </summary>
        internal VulkanDescriptorCounts Restore(in VulkanDescriptorCounts request) => Add(request);

        /// <summary>The per-type sum, saturating.</summary>
        internal VulkanDescriptorCounts Add(in VulkanDescriptorCounts other)
            => new(
                Saturating(UniformBuffer, other.UniformBuffer),
                Saturating(UniformBufferDynamic, other.UniformBufferDynamic),
                Saturating(StorageBuffer, other.StorageBuffer),
                Saturating(StorageBufferDynamic, other.StorageBufferDynamic),
                Saturating(SampledImage, other.SampledImage),
                Saturating(StorageImage, other.StorageImage),
                Saturating(Sampler, other.Sampler));

        /// <summary>The per-type maximum, which is how the largest single-set demand seen so far is
        /// accumulated.</summary>
        internal VulkanDescriptorCounts Max(in VulkanDescriptorCounts other)
            => new(
                Math.Max(UniformBuffer, other.UniformBuffer),
                Math.Max(UniformBufferDynamic, other.UniformBufferDynamic),
                Math.Max(StorageBuffer, other.StorageBuffer),
                Math.Max(StorageBufferDynamic, other.StorageBufferDynamic),
                Math.Max(SampledImage, other.SampledImage),
                Math.Max(StorageImage, other.StorageImage),
                Math.Max(Sampler, other.Sampler));

        /// <summary>Every count multiplied by <paramref name="factor"/>, saturating. What sizing a pool to hold
        /// N sets of one shape comes to.</summary>
        internal VulkanDescriptorCounts Scaled(uint factor)
            => new(
                Multiplied(UniformBuffer, factor),
                Multiplied(UniformBufferDynamic, factor),
                Multiplied(StorageBuffer, factor),
                Multiplied(StorageBufferDynamic, factor),
                Multiplied(SampledImage, factor),
                Multiplied(StorageImage, factor),
                Multiplied(Sampler, factor));

        /// <summary>The line a refusal and a diagnostic quote, naming only the non-zero types so a message about
        /// one uniform buffer is not six zeroes long.</summary>
        internal string Describe()
        {
            if (IsEmpty) return "no descriptors";

            var parts = new System.Collections.Generic.List<string>(CountedTypes.Length);
            foreach (VulkanDescriptorType type in CountedTypes)
            {
                uint count = CountOf(type);
                if (count != 0) parts.Add(count.ToString(CultureInfo.InvariantCulture) + " " + type);
            }

            return string.Join(", ", parts);
        }

        static uint Bump(uint value) => value == uint.MaxValue ? value : value + 1;

        static uint Saturating(uint a, uint b) => a > uint.MaxValue - b ? uint.MaxValue : a + b;

        static uint Multiplied(uint value, uint factor)
        {
            if (value == 0 || factor == 0) return 0;

            return value > uint.MaxValue / factor ? uint.MaxValue : value * factor;
        }
    }
}
