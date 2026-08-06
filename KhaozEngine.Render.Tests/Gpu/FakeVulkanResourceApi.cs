using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE RESOURCE SEAM WITH NO DRIVER BEHIND IT, recording every call as an event and tracking every live
    /// handle, so a test can assert what a creation and a disposal actually asked the driver for, in order.
    /// <para>
    /// This is what makes the whole of row 9's POLICY device-free: which usage bits a resource gets, which views
    /// are created and over what range, which memory ladder it allocates from, what its resting layout is, and
    /// above all that a disposal destroys every object exactly once and in the one legal order (views before their
    /// image). All of it runs under a plain <c>[Fact]</c> on a machine with no Vulkan loader.
    /// </para>
    /// <para>
    /// THE MAPPED POINTERS ARE FAKE ADDRESSES, exactly as <see cref="FakeVulkanDeviceMemoryApi"/>'s are, so a test
    /// may read a buffer's mapped BASE and its arithmetic but must never write through one. The one path that
    /// really writes (a staging texture's own region write) is driven against real pinned memory in
    /// <c>VulkanStagingMapTests</c> instead.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that a driver accepted the structures or that an image view is compatible
    /// with its image. That belongs to the <c>vulkan-native</c> CI leg (row 19,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/529). The boundary is deliberate rather than an omission.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanResourceApi : IVulkanResourceApi
    {
        readonly List<string> _log = new();
        readonly HashSet<ulong> _live = new();
        readonly Dictionary<ulong, ulong> _bufferSizes = new();
        readonly Dictionary<ulong, VulkanBufferBinding> _bufferBindings = new();
        readonly List<VulkanImageSpec> _images = new();
        readonly List<VulkanImageViewSpec> _views = new();
        readonly List<VulkanSamplerSpec> _samplers = new();
        readonly List<(ulong Resource, ulong Memory, ulong Offset)> _binds = new();

        ulong _nextBuffer = 0x1_0000;
        ulong _nextImage = 0x2_0000;
        ulong _nextView = 0x3_0000;
        ulong _nextSampler = 0x4_0000;

        /// <summary>Every call in order, as text. The device-free stand-in for a native call log.</summary>
        internal IReadOnlyList<string> Events => _log;

        /// <summary>Handles created and not yet destroyed. Non-empty after a teardown meant to release everything
        /// is a leak, and it is the assertion the disposal tests turn on.</summary>
        internal IReadOnlyCollection<ulong> Live => _live;

        /// <summary>Every image created, in creation order, with the spec it was created from.</summary>
        internal IReadOnlyList<VulkanImageSpec> Images => _images;

        /// <summary>Every image view created, in creation order. Its COUNT is decision V-M11's bound and its
        /// RANGES are the decision itself.</summary>
        internal IReadOnlyList<VulkanImageViewSpec> Views => _views;

        /// <summary>Every sampler created, in creation order, with the spec AFTER the anisotropy
        /// degradation.</summary>
        internal IReadOnlyList<VulkanSamplerSpec> Samplers => _samplers;

        /// <summary>Every bind, so a test can prove a resource was bound to the memory it allocated.</summary>
        internal IReadOnlyList<(ulong Resource, ulong Memory, ulong Offset)> Binds => _binds;

        /// <summary>The usage bits a buffer was created with.</summary>
        internal VulkanBufferBinding BindingOf(ulong buffer) => _bufferBindings[buffer];

        /// <summary>The size a buffer was created with, which for a ring-backed uniform buffer is the WHOLE
        /// allocation rather than the logical size the seam asked for.</summary>
        internal ulong SizeOf(ulong buffer) => _bufferSizes[buffer];

        /// <summary>How many events name <paramref name="call"/>.</summary>
        internal int CountOf(string call) => _log.Count(e => e.StartsWith(call, StringComparison.Ordinal));

        /// <summary>The call to fail on, once, so a test can drive the half-built failure paths.</summary>
        internal string? FailOn { get; set; }

        /// <inheritdoc/>
        public ulong CreateBuffer(ulong sizeBytes, VulkanBufferBinding binding)
        {
            FailIfAsked("vkCreateBuffer");

            ulong handle = _nextBuffer;
            _nextBuffer += 0x10;

            _live.Add(handle);
            _bufferSizes[handle] = sizeBytes;
            _bufferBindings[handle] = binding;
            _log.Add($"vkCreateBuffer {Hex(handle)} size={sizeBytes.ToString(CultureInfo.InvariantCulture)} "
                + $"usage={binding}");
            return handle;
        }

        /// <inheritdoc/>
        public VulkanResourceRequirements BufferRequirements(ulong buffer)
        {
            _log.Add($"vkGetBufferMemoryRequirements2 {Hex(buffer)}");
            return new VulkanResourceRequirements(_bufferSizes[buffer], 256, uint.MaxValue, false, false);
        }

        /// <inheritdoc/>
        public void BindBufferMemory(ulong buffer, ulong memory, ulong offset)
        {
            RequireLive(buffer, "vkBindBufferMemory");
            _binds.Add((buffer, memory, offset));
            _log.Add($"vkBindBufferMemory {Hex(buffer)}");
        }

        /// <inheritdoc/>
        public void DestroyBuffer(ulong buffer)
        {
            RequireLive(buffer, "vkDestroyBuffer");
            _live.Remove(buffer);
            _log.Add($"vkDestroyBuffer {Hex(buffer)}");
        }

        /// <inheritdoc/>
        public ulong CreateImage(in VulkanImageSpec spec)
        {
            FailIfAsked("vkCreateImage");

            ulong handle = _nextImage;
            _nextImage += 0x10;

            _live.Add(handle);
            _images.Add(spec);
            _log.Add($"vkCreateImage {Hex(handle)} {spec.Width}x{spec.Height} mips={spec.MipLevels} "
                + $"layers={spec.ArrayLayers} usage={spec.Usage}");
            return handle;
        }

        /// <inheritdoc/>
        public VulkanResourceRequirements ImageRequirements(ulong image)
        {
            _log.Add($"vkGetImageMemoryRequirements2 {Hex(image)}");

            // A plausible size rather than a real one: the allocator only needs a number, and the number a driver
            // would answer depends on tiling and on the implementation.
            VulkanImageSpec spec = _images[(int)((image - 0x2_0000) / 0x10)];
            ulong size = Math.Max(256UL, (ulong)spec.Width * spec.Height * 8 * spec.ArrayLayers);
            return new VulkanResourceRequirements(size, 256, uint.MaxValue, false, false);
        }

        /// <inheritdoc/>
        public void BindImageMemory(ulong image, ulong memory, ulong offset)
        {
            RequireLive(image, "vkBindImageMemory");
            _binds.Add((image, memory, offset));
            _log.Add($"vkBindImageMemory {Hex(image)}");
        }

        /// <inheritdoc/>
        public void DestroyImage(ulong image)
        {
            RequireLive(image, "vkDestroyImage");
            _live.Remove(image);
            _log.Add($"vkDestroyImage {Hex(image)}");
        }

        /// <inheritdoc/>
        public ulong CreateImageView(in VulkanImageViewSpec spec)
        {
            FailIfAsked("vkCreateImageView");

            ulong handle = _nextView;
            _nextView += 0x10;

            _live.Add(handle);
            _views.Add(spec);
            _log.Add($"vkCreateImageView {Hex(handle)} image={Hex(spec.Image)} mip={spec.BaseMipLevel}+"
                + $"{spec.MipLevels} layer={spec.BaseArrayLayer}+{spec.ArrayLayers}");
            return handle;
        }

        /// <inheritdoc/>
        public void DestroyImageView(ulong view)
        {
            RequireLive(view, "vkDestroyImageView");
            _live.Remove(view);
            _log.Add($"vkDestroyImageView {Hex(view)}");
        }

        /// <inheritdoc/>
        public ulong CreateSampler(in VulkanSamplerSpec spec)
        {
            FailIfAsked("vkCreateSampler");

            ulong handle = _nextSampler;
            _nextSampler += 0x10;

            _live.Add(handle);
            _samplers.Add(spec);
            _log.Add($"vkCreateSampler {Hex(handle)} filter={spec.Filter} u={spec.AddressU}");
            return handle;
        }

        /// <inheritdoc/>
        public void DestroySampler(ulong sampler)
        {
            RequireLive(sampler, "vkDestroySampler");
            _live.Remove(sampler);
            _log.Add($"vkDestroySampler {Hex(sampler)}");
        }

        void FailIfAsked(string call)
        {
            if (FailOn != call) return;

            FailOn = null;
            throw new InvalidOperationException($"The fake native Vulkan resource seam was told to fail {call}.");
        }

        // A destroy of something that is not live is a DOUBLE DESTROY, which is the one class of defect a deferred
        // disposal design can produce silently and which every disposal test here exists to catch.
        void RequireLive(ulong handle, string call)
        {
            if (_live.Contains(handle)) return;

            throw new InvalidOperationException(
                $"{call} was called on {Hex(handle)}, which is not live. Either it was never created or it has "
                + "already been destroyed, and a double destroy through the retire list is exactly the defect a "
                + "deferred disposal produces without saying anything.");
        }

        static string Hex(ulong handle) => "0x" + handle.ToString("x", CultureInfo.InvariantCulture);
    }
}
