using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A MIP CHAIN SEEDED AND GENERATED MORE THAN ONCE INTO ONE COMMAND LIST, DEVICE-FREE. Its own type rather
    /// than another case in <see cref="VulkanTransferPathTests"/> because the question is different: that class
    /// pins what ONE transfer emits, and this one pins what the SECOND recording of the same composition finds
    /// waiting for it, which is a property of the layout tracker's accumulated map rather than of any one call.
    ///
    /// <para><b>THE SHAPE COMES FROM THE OCEAN AND IT WAS RED FOR A RELEASE WINDOW.</b>
    /// <c>OceanFftProducer.BuildMipChain</c> seeds every layer of the cascade map and then calls
    /// <c>GenerateMipmaps</c>, once per RECORDING. The generation names mip 0 over every layer, which collapses
    /// the per-layer entries the seeding copies left into one wider entry, so the next round's per-layer copies
    /// ask for a range CONTAINED in a tracked one. The tracker classified that as a partial overlap and refused
    /// it, which took eight <c>WaterClipmapAcceptanceTests</c> rows and
    /// <c>WaterSurfProbe.Clipmap_boundary_step_height_maps</c> down on the vulkan-native leg and nowhere else
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/623).</para>
    ///
    /// <para><b>WHY NO OTHER LEG SAW IT.</b> The refusal is this backend's own bookkeeping rather than anything
    /// the driver reports, so the incumbent Veldrid Vulkan leg passes these same rows on the same lavapipe, and
    /// Metal and Direct3D 11 never reach this code at all. That is the guest-leg design working: a defect in the
    /// engine's own Vulkan backend, found by running the incumbent's golden family against it.</para>
    /// </summary>
    public sealed class VulkanRepeatedChainTests
    {
        /// <summary>
        /// THE SECOND SEED ROUND IS ANSWERED OVER THE COLLAPSED ENTRY, in one barrier covering every layer that
        /// entry holds, and the rest of the round then costs nothing because the entry it sits in is already in
        /// the transfer layout.
        /// <para>
        /// THE BARRIER WIDENS TO THE ENTRY RATHER THAN THE ENTRY SPLITTING TO THE REQUEST, which is sound because
        /// every subresource it moves is already inside an entry THIS recording put there: nothing at rest is
        /// touched, the entry stays uniform, and <c>End</c> still restores it in one barrier. Splitting instead
        /// would mean naming the entry minus the request, which is the rectangle subtraction the tracker refuses
        /// to do, and it would trade one entry for up to four.
        /// </para>
        /// </summary>
        [Fact]
        public void ASecondSeedRoundAfterAMipGeneration_MovesTheCollapsedEntry()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture seed = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled, arrayLayers: 2));
                owned.Add(seed);
                IGpuTexture array = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 3,
                    arrayLayers: 2));
                owned.Add(array);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                // ROUND ONE, the shipped shape: every layer seeded, then the chain walked. The generation names
                // mip 0 over BOTH layers, which collapses the two per-layer entries the copies left into one.
                for (uint layer = 0; layer < 2; layer++)
                    list.CopyTextureSubresource(seed, 0, layer, array, 0, layer, 8, 8);

                list.GenerateMipmaps(array);

                // ROUND TWO, the next frame recorded into the same list: one layer of a mip 0 now held whole.
                int before = fixture.Barriers.BarrierCount;
                list.CopyTextureSubresource(seed, 0, 0, array, 0, 0, 8, 8);

                ImageMemoryBarrier2 moved = Assert.Single(fixture.Barriers.Barriers.Skip(before));
                Assert.Equal(ImageLayout.TransferSrcOptimal, moved.OldLayout);
                Assert.Equal(ImageLayout.TransferDstOptimal, moved.NewLayout);
                Assert.Equal(0u, moved.SubresourceRange.BaseArrayLayer);
                Assert.Equal(2u, moved.SubresourceRange.LayerCount);
                Assert.Equal(1u, moved.SubresourceRange.LevelCount);

                // AND THE REST OF THE ROUND COSTS NOTHING, because both sides are already where they need to be.
                int after = fixture.Barriers.BarrierCount;
                list.CopyTextureSubresource(seed, 0, 1, array, 0, 1, 8, 8);

                Assert.Equal(after, fixture.Barriers.BarrierCount);

                // AND THE CHAIN WALKS AGAIN, which is the round after that one, and the list closes clean.
                list.GenerateMipmaps(array);
                list.End();
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        static void DisposeAll(List<IDisposable> owned)
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }
}
