using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ONE <c>vkCmdBindDescriptorSets</c> AS THE DRIVER WOULD HAVE RECEIVED IT. The counting sink in the backend
    /// tallies how many, which is what a BUDGET is over. This records WHAT, which is what the positional
    /// dynamic-offset array needs: a count alone cannot tell a correctly composed array from one whose entries
    /// landed on the wrong descriptors.
    /// </summary>
    /// <param name="BindPoint">Graphics or compute.</param>
    /// <param name="PipelineLayout">The <c>VkPipelineLayout</c> the run was bound under.</param>
    /// <param name="FirstSet">The run's starting set number.</param>
    /// <param name="Sets">The <c>VkDescriptorSet</c> handles, in slot order.</param>
    /// <param name="DynamicOffsets">The positional array, in set-then-binding order.</param>
    internal readonly record struct VulkanRecordedBind(
        PipelineBindPoint BindPoint, ulong PipelineLayout, uint FirstSet, ulong[] Sets, uint[] DynamicOffsets);

    /// <summary>
    /// AN <see cref="IVkCmdSink"/> THAT KEEPS THE ARGUMENTS. A readonly struct over one list, for the same reason
    /// every sink in the backend is one: it is copied into whatever recorder drives it, so its state sits behind a
    /// reference and two copies still append to one log.
    /// </summary>
    internal readonly struct VulkanCapturingCmdSink : IVkCmdSink
    {
        readonly List<VulkanRecordedBind> _binds;

        internal VulkanCapturingCmdSink(List<VulkanRecordedBind> binds) => _binds = binds;

        /// <inheritdoc/>
        public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets)
        {
            var handles = new ulong[sets.Length];
            for (int i = 0; i < sets.Length; i++) handles[i] = sets[i].Handle;

            _binds.Add(new VulkanRecordedBind(bindPoint, layout.Handle, firstSet, handles,
                dynamicOffsets.ToArray()));
        }

        /// <inheritdoc/>
        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        {
        }

        /// <inheritdoc/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance)
        {
        }

        /// <inheritdoc/>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
        }

        /// <inheritdoc/>
        public void PipelineBarrier(in DependencyInfo dependency)
        {
        }
    }

    /// <summary>A pipeline's layout as the bind schedule sees it: the shared <c>VkPipelineLayout</c> and the
    /// set-layout handles it was created from, which is exactly the pair row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523) will hand
    /// <c>VulkanBindRecords.SetPipelineLayout</c>.</summary>
    internal readonly record struct VulkanBoundPipeline(ulong Layout, ulong[] SetLayouts);

    /// <summary>
    /// THE DEVICE-FREE RIG FOR ROW 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521): real resource
    /// layouts on the real content-dedup cache, real descriptor sets with their descriptors resolved at creation,
    /// real uniform rings with real segment arithmetic, and real pipeline layouts. Only the two native seams under
    /// <see cref="VulkanResourceFixture"/> are faked.
    /// <para>
    /// EVERY SHAPE IS BUILT FROM <see cref="VulkanDescriptorLimitTests.ShippedLayouts"/>, which is row 10's
    /// transcription of all thirty-three shipped <c>CreateResourceLayout</c> sites with their source lines. A rig
    /// that invented its own layouts would be pinning arithmetic nothing ships.
    /// </para>
    /// </summary>
    internal sealed class VulkanBindHarness : IDisposable
    {
        readonly List<IDisposable> _owned = new();

        internal VulkanBindHarness(int framesInFlight = 3)
            => Fixture = new VulkanResourceFixture(framesInFlight);

        /// <summary>The device-free rig everything here is built on.</summary>
        internal VulkanResourceFixture Fixture { get; }

        /// <summary>The device's ring allocator, whose <c>BeginFrame</c> is what moves every ring base at
        /// once.</summary>
        internal VulkanRingAllocator Rings => Fixture.Rings;

        /// <summary>A real <see cref="VulkanResourceLayout"/> for a shipped layout name, on the device's own
        /// content-dedup cache, so two names with identical content really do come back sharing one
        /// <c>VkDescriptorSetLayout</c>.</summary>
        internal VulkanResourceLayout Layout(string shipped)
        {
            var layout = (VulkanResourceLayout)Fixture.Factory.CreateResourceLayout(
                VulkanDescriptorLimitTests.ShippedLayouts[shipped]);
            _owned.Add(layout);
            return layout;
        }

        /// <summary>A real <see cref="VulkanResourceSet"/> over a shipped layout, with a freshly created resource
        /// of the right kind at every element.</summary>
        internal VulkanResourceSet Set(string shipped)
            => (VulkanResourceSet)Fixture.CreateSetFor(VulkanDescriptorLimitTests.ShippedLayouts[shipped], _owned);

        /// <summary>
        /// A set built the way the five shipped renderers that pass a non-zero dynamic offset build theirs: one
        /// declared-dynamic uniform element bound to a <see cref="GpuBufferRange"/> that is ONE slot of a buffer
        /// holding several, so the caller's per-draw offset selects the slot. This is the shape V-M6's invariant is
        /// really about, and the shape the whole-buffer sets above cannot exercise.
        /// </summary>
        /// <param name="slotBytes">One slot's size, which becomes the descriptor's range.</param>
        /// <param name="slots">How many slots the buffer holds.</param>
        internal VulkanResourceSet WindowedSet(uint slotBytes, uint slots)
        {
            IGpuResourceLayout layout = Fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout(dynamic: true));
            _owned.Add(layout);

            IGpuBuffer buffer = Fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(slotBytes * slots, GpuBufferUsage.UniformBuffer));
            _owned.Add(buffer);

            var set = (VulkanResourceSet)Fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, new GpuBufferRange(buffer, 0, slotBytes)));
            _owned.Add(set);
            return set;
        }

        /// <summary>
        /// A set over an ARBITRARY description and arbitrary resources, for the shapes no shipped renderer has and
        /// the positional rule still has to hold for: SEVERAL dynamic descriptors in one set, and a uniform buffer
        /// sitting after a texture in binding order. The heaviest shipped pipeline spends exactly one dynamic
        /// uniform descriptor, so the multi-entry case is precisely the one the shipped table cannot reach and
        /// precisely the one an off-by-one in a positional array shows up in.
        /// </summary>
        internal VulkanResourceSet CustomSet(in GpuResourceLayoutDescription description,
            params IGpuBindableResource[] resources)
        {
            IGpuResourceLayout layout = Fixture.Factory.CreateResourceLayout(description);
            _owned.Add(layout);

            var set = (VulkanResourceSet)Fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, resources));
            _owned.Add(set);
            return set;
        }

        /// <summary>A ring-backed uniform buffer of a chosen size, so two rings in one set can have DIFFERENT
        /// segment strides and their bases are therefore distinguishable past segment zero.</summary>
        internal IGpuBuffer UniformBuffer(uint sizeInBytes)
        {
            IGpuBuffer buffer = Fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(sizeInBytes, GpuBufferUsage.UniformBuffer));
            _owned.Add(buffer);
            return buffer;
        }

        /// <summary>A sampled texture, for a layout that puts one between two uniform buffers.</summary>
        internal IGpuTexture SampledTexture()
        {
            IGpuTexture texture = Fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));
            _owned.Add(texture);
            return texture;
        }

        /// <summary>
        /// The shared <c>VkPipelineLayout</c> for a pipeline declaring these shipped layouts in slot order, plus
        /// the set-layout handles it was created from. Exactly what row 13 will hand the bind records.
        /// </summary>
        internal VulkanBoundPipeline Pipeline(params string[] shipped)
        {
            var layouts = new VulkanResourceLayout[shipped.Length];
            var handles = new ulong[shipped.Length];
            for (int i = 0; i < shipped.Length; i++)
            {
                layouts[i] = Layout(shipped[i]);
                handles[i] = layouts[i].SetLayout;
            }

            return new VulkanBoundPipeline(Fixture.Descriptors.PipelineLayouts.GetOrCreate(layouts), handles);
        }

        /// <summary>A pipeline layout over set layouts taken straight off sets, for the tests that build their
        /// sets first and want a layout those sets satisfy.</summary>
        internal VulkanBoundPipeline PipelineFor(params VulkanResourceSet[] sets)
        {
            var handles = new ulong[sets.Length];
            int dynamicUniforms = 0;
            for (int i = 0; i < sets.Length; i++)
            {
                handles[i] = sets[i].Layout.SetLayout;
                dynamicUniforms += sets[i].Layout.DynamicUniformCount;
            }

            return new VulkanBoundPipeline(
                Fixture.Descriptors.PipelineLayouts.GetOrCreate(handles, dynamicUniforms), handles);
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
            Fixture.Descriptors.DestroyAll();
        }
    }
}
