using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE <c>CopyBuffer</c> OFFSET CONTRACT, ON ALL FOUR BACKENDS AT ONCE, DEVICE-FREE
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/684">#684</see>, which resolves
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/602">#602</see>). Both offsets must be
    /// multiples of four and an offset that is not is refused, identically, whichever backend the caller
    /// happens to be running on.
    ///
    /// <para><b>WHY THIS IS ONE FILE RATHER THAN A ROW IN EACH BACKEND'S OWN SUITE.</b> The claim is not "Metal
    /// refuses" or "Vulkan refuses". It is that the FOUR AGREE, and a claim about agreement can only be asserted
    /// where the four are side by side. Each backend's own transfer suite still owns everything else about its
    /// copy, and <c>MetalTransferPathTests</c> keeps the two rows that pin the side-naming, because naming the
    /// wrong end sends a caller to inspect the buffer that was fine.</para>
    ///
    /// <para><b>THE INCUMBENT IS IN THE TABLE ON PURPOSE, AND THAT IS THE PART WITH A SHELF LIFE.</b> Veldrid,
    /// native Vulkan and native Direct3D 11 all TOOK an unaligned offset before this landed, and only native
    /// Metal refused, because macOS requires the alignment of
    /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>. Tightening the seam instead of
    /// loosening Metal is the direction that cannot silently return the wrong bytes, and doing it while the
    /// incumbent is still here is what makes a green suite evidence that nothing in the engine ever leaned on
    /// the tolerant behaviour. When the incumbent goes, its two rows go with it and the other three stay.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either a backend stopped enforcing the rule, or one of them started
    /// wording its refusal differently, or the rule stopped being four bytes on one side of the seam and not the
    /// other, or an aligned copy stopped being recorded at all.</para>
    /// </summary>
    public sealed class CopyBufferOffsetContractTests : IDisposable
    {
        // The sentence every backend's refusal carries, because they all come out of one helper. Asserting the
        // shared text is what makes "identically" a claim rather than a hope: four separately worded refusals
        // would all still be ArgumentOutOfRangeExceptions.
        const string SharedSentence =
            "IGpuCommandList.CopyBuffer requires that of both of its offsets on every backend";

        const uint Unaligned = 3;
        const uint UnalignedDestination = 6;
        const uint SmallestAligned = 4;
        const uint CopyBytes = 16;

        readonly MetalRingHarness _metal = new();
        readonly List<IDisposable> _owned = new();

        /// <inheritdoc/>
        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _metal.Dispose();
        }

        /// <summary>
        /// THE RULE IS FOUR BYTES AND THERE IS ONE OF IT. <c>MetalCopyAlignment</c> keeps its own name because it
        /// also carries the SIZE padding, which is Metal's alone, but its offset half is the seam's constant now.
        /// Two constants that happened to agree is exactly the drift the forward exists to prevent.
        /// </summary>
        [Fact]
        public void TheSeamRule_IsFourBytes_AndMetalReadsTheSameConstant()
        {
            Assert.Equal(4u, GpuCopyAlignment.Bytes);
            Assert.Equal((ulong)GpuCopyAlignment.Bytes, MetalCopyAlignment.Bytes);
            Assert.True(GpuCopyAlignment.IsAligned(0));
            Assert.True(GpuCopyAlignment.IsAligned(SmallestAligned));
            Assert.False(GpuCopyAlignment.IsAligned(Unaligned));
        }

        /// <summary>
        /// EVERY BACKEND REFUSES AN UNALIGNED SOURCE OFFSET, AND SAYS THE SAME THING. The parameter name is the
        /// seam's own <c>srcOffsetBytes</c> on all four, so a caller catching the exception reads one name
        /// whichever machine reported it.
        /// </summary>
        [Fact]
        public void EveryBackend_RefusesAnUnalignedSourceOffset_InTheSameWords()
        {
            foreach ((string backend, Action<uint, uint> copy) in EveryBackend())
            {
                ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                    () => copy(Unaligned, 0));

                Assert.Equal("srcOffsetBytes", thrown.ParamName);
                Assert.Contains("source offset of 3", thrown.Message, StringComparison.Ordinal);
                Assert.Contains("not a multiple of 4", thrown.Message, StringComparison.Ordinal);
                Assert.Contains(SharedSentence, thrown.Message, StringComparison.Ordinal);

                // The side-naming is the whole point of passing it through: a refusal that named the other end
                // sends the reader to the buffer that was fine.
                Assert.DoesNotContain("destination offset", thrown.Message, StringComparison.Ordinal);
                Assert.Contains(backend, thrown.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// AND THE DESTINATION END, SEPARATELY, WITH AN ALIGNED SOURCE, so only one of the two offsets can be the
        /// cause. A single row covering both would pass on a backend that checked one of them twice.
        /// </summary>
        [Fact]
        public void EveryBackend_RefusesAnUnalignedDestinationOffset_InTheSameWords()
        {
            foreach ((string backend, Action<uint, uint> copy) in EveryBackend())
            {
                ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                    () => copy(SmallestAligned, UnalignedDestination));

                Assert.Equal("dstOffsetBytes", thrown.ParamName);
                Assert.Contains("destination offset of 6", thrown.Message, StringComparison.Ordinal);
                Assert.Contains(SharedSentence, thrown.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("source offset", thrown.Message, StringComparison.Ordinal);
                Assert.Contains(backend, thrown.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// AND THE SMALLEST ALIGNED OFFSET IS STILL TAKEN, on each of the three backends whose copy can be
        /// recorded without a device. The refusal is a four-byte rule and not a rounding to something wider, so a
        /// guard that hardened into "only zero" or "only a multiple of sixteen" is red here rather than
        /// discovered by a consumer. The incumbent's accept side needs a real
        /// <c>Veldrid.CommandList</c> under it and is covered by <c>CopyBufferOffsetGpuTests</c> on the two
        /// incumbent CI legs.
        /// </summary>
        [Fact]
        public void TheThreeNativeBackends_RecordACopyAtTheSmallestAlignedOffset()
        {
            MetalCommandList metal = NewMetalList();
            MetalBuffer metalSource = _metal.NewBuffer(256, GpuBufferUsage.StructuredBufferReadWrite);
            MetalBuffer metalDestination = _metal.NewBuffer(256, GpuBufferUsage.Staging);
            metal.Begin();
            metal.CopyBuffer(metalSource, SmallestAligned, metalDestination, SmallestAligned, CopyBytes);
            (_, _, ulong metalFrom, _, ulong metalTo, ulong metalSize) = Assert.Single(_metal.Blit.Copies);
            Assert.Equal(SmallestAligned, (uint)metalFrom);
            Assert.Equal(SmallestAligned, (uint)metalTo);
            Assert.Equal(CopyBytes, (uint)metalSize);

            var vulkan = new VulkanResourceFixture();
            using VulkanCommandList vulkanList = vulkan.CreateList();
            IGpuBuffer vulkanSource = VulkanBuffer(vulkan, GpuBufferUsage.StructuredBufferReadWrite);
            IGpuBuffer vulkanDestination = VulkanBuffer(vulkan, GpuBufferUsage.Staging);
            vulkanList.Begin();
            vulkanList.CopyBuffer(vulkanSource, SmallestAligned, vulkanDestination, SmallestAligned, CopyBytes);
            Assert.Equal(SmallestAligned,
                (uint)Assert.Single(vulkan.TransferSink.BufferCopies).Region.SrcOffset);

            var fixtures = new D3D11RecordingDriverTests.Fixtures();
            using D3D11CommandRecorder<D3D11StreamEmitter> d3d11 = D3D11CommandDrivers.CreateDeferred();
            d3d11.Begin();
            int before = d3d11.Emitter.Stream.Count;
            d3d11.CopyBuffer(fixtures.Uniforms, SmallestAligned, fixtures.Staging, SmallestAligned, CopyBytes);
            Assert.Equal(before + 1, d3d11.Emitter.Stream.Count);
        }

        /// <summary>
        /// <see cref="GpuReadback.ReadBuffer{T}"/> IS WHERE THE DIVERGENCE REACHED A CONSUMER, so it refuses on
        /// its own account rather than letting the copy do it three layers down: the caller reads a message
        /// naming the parameter they passed, and no staging buffer, command list, submission or drain happens
        /// first. A null device is what asserts the ordering. Without the check it is a
        /// <see cref="NullReferenceException"/> off <c>gd.Factory</c> instead.
        /// </summary>
        [Fact]
        public void ReadBuffer_RefusesAnUnalignedOffset_BeforeItTouchesTheDevice()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuReadback.ReadBuffer<uint>(null!, null!, 4, Unaligned));

            Assert.Equal("srcOffsetBytes", thrown.ParamName);
            Assert.Contains("A buffer readback (GpuReadback.ReadBuffer)", thrown.Message, StringComparison.Ordinal);
            Assert.Contains(SharedSentence, thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// AND THE SAME THROUGH A DEVICE THAT WOULD OTHERWISE HAVE ANSWERED. <see cref="FakeGpuDevice"/> drops
        /// every command and refuses only the map at the very end, so the guard's absence here is a
        /// <see cref="NotSupportedException"/> from the map rather than a success: the point of the row is that
        /// the exception a consumer sees is about their argument, not about the fake three calls later.
        /// </summary>
        [Fact]
        public void ReadBuffer_RefusesAnUnalignedOffset_OnADeviceThatDropsEveryCommand()
        {
            using var device = new FakeGpuDevice();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuReadback.ReadBuffer<uint>(device, buffer, 4, Unaligned));
        }

        /// <summary>
        /// A ZERO-ELEMENT READ IS STILL AN INVALID OFFSET. The early return for an empty result sits BELOW the
        /// check, deliberately: an argument the seam cannot accept is a caller bug whether or not this particular
        /// call would have copied anything, and a rule that only fires on non-empty reads is one a consumer
        /// discovers late.
        /// </summary>
        [Fact]
        public void ReadBuffer_RefusesAnUnalignedOffset_EvenForZeroElements()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuReadback.ReadBuffer<uint>(null!, null!, 0, Unaligned));

        // ---- The four backends, as one table -------------------------------------------------------------

        // Each entry copies CopyBytes with the two offsets it is handed, on a command list of its own, through
        // the real seam implementation. The string is a fragment of the "what" that backend names itself with,
        // so a refusal really did come from the backend the row claims.
        IEnumerable<(string Backend, Action<uint, uint> Copy)> EveryBackend()
        {
            yield return ("Metal", (src, dst) =>
            {
                MetalCommandList list = NewMetalList();
                MetalBuffer source = _metal.NewBuffer(256, GpuBufferUsage.StructuredBufferReadWrite);
                MetalBuffer destination = _metal.NewBuffer(256, GpuBufferUsage.Staging);
                list.Begin();
                list.CopyBuffer(source, src, destination, dst, CopyBytes);
            });

            yield return ("Vulkan", (src, dst) =>
            {
                var fixture = new VulkanResourceFixture();
                using VulkanCommandList list = fixture.CreateList();
                IGpuBuffer source = VulkanBuffer(fixture, GpuBufferUsage.StructuredBufferReadWrite);
                IGpuBuffer destination = VulkanBuffer(fixture, GpuBufferUsage.Staging);
                list.Begin();
                list.CopyBuffer(source, src, destination, dst, CopyBytes);
            });

            yield return ("Direct3D 11", (src, dst) =>
            {
                var fixtures = new D3D11RecordingDriverTests.Fixtures();
                using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
                list.Begin();
                list.CopyBuffer(fixtures.Uniforms, src, fixtures.Staging, dst, CopyBytes);
            });
        }

        MetalCommandList NewMetalList()
        {
            MetalCommandList list = _metal.NewList(new object());
            _owned.Add(list);
            return list;
        }

        IGpuBuffer VulkanBuffer(VulkanResourceFixture fixture, GpuBufferUsage usage)
        {
            IGpuBuffer buffer = fixture.Factory.CreateBuffer(VulkanResourceFixture.Buffer(256, usage));
            _owned.Add(buffer);
            return buffer;
        }
    }
}
