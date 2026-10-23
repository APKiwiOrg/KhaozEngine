using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Graphics storage accesses participate in the same in-list dependencies as compute accesses.</summary>
    public sealed class VulkanGraphicsBufferHazardTests : IDisposable
    {
        readonly VulkanResourceFixture _fixture = new();
        readonly List<IDisposable> _owned = new();
        readonly VulkanCommandList _list;

        public VulkanGraphicsBufferHazardTests()
        {
            IGpuTexture target = Own(_fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.RenderTarget)));
            IGpuFramebuffer framebuffer = Own(_fixture.Factory.CreateFramebuffer(null, target));
            _list = _fixture.CreateList();
            _list.Begin();
            _list.SetFramebuffer(framebuffer);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AGraphicsReadOrdersALaterDispatchWrite(bool indexed)
        {
            IGpuBuffer shared = Buffer();
            BindGraphics(Set(shared, false, GpuShaderStages.Vertex | GpuShaderStages.Fragment));
            Draw(indexed);
            Draw(indexed);
            Assert.Equal(0, _fixture.DrawEmitter.DependencyBarrierCount);
            Assert.Equal(0, _list.Draws.Hazards.WrittenCount);

            BindCompute(Set(shared, true, GpuShaderStages.Compute));
            _fixture.Trace.Clear();
            _list.Dispatch(1, 1, 1);

            Assert.Equal(1, _fixture.DrawEmitter.DependencyBarrierCount);
            AssertOrder("EndRendering", "DependencyBarrier()", "Dispatch(1,1,1)");
            AssertShaderStages();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void AFragmentWriteOrdersALaterReader(bool indexed, bool computeReader)
        {
            IGpuBuffer shared = Buffer();
            BindGraphics(Set(shared, true, GpuShaderStages.Fragment));
            Draw(indexed);

            _fixture.Trace.Clear();
            if (computeReader)
            {
                BindCompute(Set(shared, false, GpuShaderStages.Compute));
                _list.Dispatch(1, 1, 1);
                AssertOrder("EndRendering", "DependencyBarrier()", "Dispatch(1,1,1)");
            }
            else
            {
                BindGraphics(Set(shared, false, GpuShaderStages.Vertex));
                Draw(indexed);
                AssertOrder("EndRendering", "DependencyBarrier()", "BeginRendering(16x16,colour=1,depth=none)",
                    indexed ? "DrawIndexed(3,1)" : "Draw(3,1)");
                Draw(indexed);
            }

            Assert.Equal(1, _fixture.DrawEmitter.DependencyBarrierCount);
            AssertShaderStages();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnIndependentDispatchDoesNotOrderAwayAnEarlierGraphicsAccess(bool writable)
        {
            IGpuBuffer shared = Buffer();
            IGpuBuffer independent = Buffer();
            BindGraphics(Set(shared, writable, GpuShaderStages.Fragment));
            _list.Draw(3);

            BindCompute(Set(independent, true, GpuShaderStages.Compute));
            _list.Dispatch(1, 1, 1);
            Assert.Equal(0, _fixture.DrawEmitter.DependencyBarrierCount);

            BindCompute(Set(shared, !writable, GpuShaderStages.Compute));
            _list.Dispatch(1, 1, 1);
            Assert.Equal(1, _fixture.DrawEmitter.DependencyBarrierCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADrawRecordsNoAccessForASetItsPipelineDropped(bool writable)
        {
            IGpuBuffer kept = Buffer();
            IGpuBuffer dropped = Buffer();
            IGpuResourceSet keptSet = Set(kept, writable, GpuShaderStages.Fragment);
            IGpuResourceSet droppedSet = Set(dropped, writable, GpuShaderStages.Fragment);
            Adopt(_list.GraphicsBinds, keptSet, droppedSet);
            _list.SetGraphicsResourceSet(0, keptSet);
            _list.SetGraphicsResourceSet(1, droppedSet);
            Adopt(_list.GraphicsBinds, keptSet);
            _list.Draw(3);

            BindCompute(Set(dropped, !writable, GpuShaderStages.Compute));
            _list.Dispatch(1, 1, 1);
            Assert.Equal(0, _fixture.DrawEmitter.DependencyBarrierCount);

            BindCompute(Set(kept, !writable, GpuShaderStages.Compute));
            _list.Dispatch(1, 1, 1);
            Assert.Equal(1, _fixture.DrawEmitter.DependencyBarrierCount);
        }

        [Fact]
        public void AGlobalBarrierPublishesEveryEarlierFragmentWriteToBothBindPoints()
        {
            IGpuBuffer first = Buffer();
            IGpuBuffer second = Buffer();
            IGpuResourceSet firstSet = Set(first, true, GpuShaderStages.Fragment);
            IGpuResourceSet secondSet = Set(second, true, GpuShaderStages.Fragment);
            Adopt(_list.GraphicsBinds, firstSet, secondSet);
            _list.SetGraphicsResourceSet(0, firstSet);
            _list.SetGraphicsResourceSet(1, secondSet);
            _list.Draw(3);

            BindCompute(Set(first, false, GpuShaderStages.Compute));
            _list.Dispatch(1, 1, 1);
            Assert.Equal(1, _fixture.DrawEmitter.DependencyBarrierCount);
            Assert.Equal(0, _list.Draws.Hazards.WrittenCount);

            BindGraphics(Set(second, false, GpuShaderStages.Vertex));
            _list.Draw(3);
            Assert.Equal(1, _fixture.DrawEmitter.DependencyBarrierCount);
            AssertShaderStages();
        }

        static void AssertShaderStages()
        {
            MemoryBarrier2 barrier = VulkanDispatchBarrier.ReadAfterWrite;
            const PipelineStageFlags2 Stages = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.AllGraphicsBit;
            Assert.Equal(Stages, barrier.SrcStageMask);
            Assert.Equal(AccessFlags2.ShaderWriteBit, barrier.SrcAccessMask);
            Assert.Equal(Stages, barrier.DstStageMask);
            Assert.Equal(AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit, barrier.DstAccessMask);
        }

        void Draw(bool indexed)
        {
            if (indexed) _list.DrawIndexed(3, 1, 0, 0, 0);
            else _list.Draw(3);
        }

        void AssertOrder(params string[] calls)
        {
            int previous = -1;
            foreach (string call in calls)
            {
                int current = _fixture.Trace.IndexOf(call);
                Assert.True(current > previous, string.Join(" | ", _fixture.Trace));
                previous = current;
            }
        }

        IGpuBuffer Buffer() => Own(_fixture.Factory.CreateBuffer(
            VulkanResourceFixture.Buffer(256, GpuBufferUsage.StructuredBufferReadWrite)));

        IGpuResourceSet Set(IGpuBuffer buffer, bool writable, GpuShaderStages stages)
        {
            IGpuResourceLayout layout = Own(_fixture.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("B", writable ? GpuResourceKind.StructuredBufferReadWrite
                    : GpuResourceKind.StructuredBufferReadOnly, stages))));
            return Own(_fixture.Factory.CreateResourceSet(new GpuResourceSetDescription(layout, buffer)));
        }

        void BindGraphics(IGpuResourceSet set)
        {
            Adopt(_list.GraphicsBinds, set);
            _list.SetGraphicsResourceSet(0, set);
        }

        void BindCompute(IGpuResourceSet set)
        {
            Adopt(_list.ComputeBinds, set);
            _list.SetComputeResourceSet(0, set);
        }

        void Adopt(VulkanBindRecords records, params IGpuResourceSet[] sets)
        {
            var handles = new ulong[sets.Length];
            for (int i = 0; i < sets.Length; i++) handles[i] = ((VulkanResourceSet)sets[i]).Layout.SetLayout;
            records.SetPipelineLayout(_fixture.Descriptors.PipelineLayouts.GetOrCreate(handles, 0), handles);
        }

        T Own<T>(T resource) where T : IDisposable
        {
            _owned.Add(resource);
            return resource;
        }

        public void Dispose()
        {
            _list.Dispose();
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        }
    }
}
