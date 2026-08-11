using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE-FREE HALF OF <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/603">#603</see>: which
    /// resolves <see cref="RenderResources"/> asks for, from which source into which destination, in what order.
    /// No GPU, so it runs on the ordinary push-path suite rather than only on a leg with a device.
    ///
    /// <para><b>IT COVERS THE HALF A DEVICE CANNOT HELP WITH.</b> A resolve DELETED, a pair reduced to one, a
    /// source and destination swapped, or the normal resolved on the early blob-shadow path where it would publish
    /// an incomplete one are all source-code faults, visible in what the pass records. The half that needs a device
    /// is whether a recorded resolve actually REACHES its destination, which is
    /// <c>MsaaResolveTargetGoldenTests</c>, and the two together are what the golden family's coverage claim
    /// promised and did not deliver: a 32x18 averaged grid of the final image stayed inside its own tolerance with
    /// the whole depth/normal pair silently discarded.</para>
    ///
    /// <para><b>THE ORDER IS ASSERTED, NOT JUST THE COUNT.</b> The two resolves of
    /// <see cref="RenderResources.ResolveDepthNormal"/> are issued BACK TO BACK, which is the shape that found the
    /// row 14 defect: the first of a back-to-back pair is the one a backend can drop while the second still lands,
    /// so a test that only counted two would pass on a backend that ran one of them twice.</para>
    /// </summary>
    public sealed class MsaaResolveWiringTests
    {
        const int W = 64, H = 48, Samples = 4;

        /// <summary>
        /// The depth/normal pair is exactly two resolves, depth FIRST, each from its own multisampled MRT
        /// attachment into the matching single-sample target the post chain and the decal pass sample.
        /// </summary>
        [Fact]
        public void TheDepthNormalPairResolvesBothAttachmentsIntoTheirOwnDestinations()
        {
            using var device = new FakeGpuDevice();
            using var res = new RenderResources(device, W, H, hdrColor: true);
            res.Resize(W, H, mipped: false, sampleCount: Samples, bloomEnabled: false, hdrColor: true);
            Assert.True(res.Msaa, "the fixture must be on the MSAA path or it asserts nothing");

            var cl = new RecordingGpuCommandList(new NullGpuCommandList());
            res.ResolveDepthNormal(cl);

            Assert.Equal(2, cl.Resolves.Count);
            Assert.Same(res.MsDepthColor, cl.Resolves[0].Source);
            Assert.Same(res.DepthColorTex, cl.Resolves[0].Destination);
            Assert.Same(res.MsNormal, cl.Resolves[1].Source);
            Assert.Same(res.NormalTex, cl.Resolves[1].Destination);
        }

        /// <summary>
        /// The early path resolves the DEPTH ONLY. It runs before the billboards, beams, trails and overlay meshes
        /// have written the normal attachment, so resolving the normal there would publish an incomplete one, and
        /// that omission is a decision rather than an oversight (see <see cref="RenderResources.ResolveDepth"/>).
        /// </summary>
        [Fact]
        public void TheEarlyPathResolvesTheDepthAndDeliberatelyNotTheNormal()
        {
            using var device = new FakeGpuDevice();
            using var res = new RenderResources(device, W, H, hdrColor: true);
            res.Resize(W, H, mipped: false, sampleCount: Samples, bloomEnabled: false, hdrColor: true);

            var cl = new RecordingGpuCommandList(new NullGpuCommandList());
            res.ResolveDepth(cl);

            Assert.Single(cl.Resolves);
            Assert.Same(res.MsDepthColor, cl.Resolves[0].Source);
            Assert.Same(res.DepthColorTex, cl.Resolves[0].Destination);
        }

        /// <summary>
        /// The colour resolve is its own call and lands AFTER the decals, water and particles that write colour,
        /// which is why it is not part of the depth/normal pair. One resolve, colour only.
        /// </summary>
        [Fact]
        public void TheColourResolveIsSeparateAndTouchesOnlyTheColourTarget()
        {
            using var device = new FakeGpuDevice();
            using var res = new RenderResources(device, W, H, hdrColor: true);
            res.Resize(W, H, mipped: false, sampleCount: Samples, bloomEnabled: false, hdrColor: true);

            var cl = new RecordingGpuCommandList(new NullGpuCommandList());
            res.ResolveColor(cl);

            Assert.Single(cl.Resolves);
            Assert.Same(res.MsColor, cl.Resolves[0].Source);
            Assert.Same(res.ColorTex, cl.Resolves[0].Destination);
        }

        /// <summary>
        /// SINGLE-SAMPLE RECORDS NOTHING, from all three, because there the targets ARE the MRT attachments and a
        /// resolve would be a copy of a texture onto itself. Asserted rather than assumed: the no-op arm is the one
        /// that would turn into a refusal from the backend (a resolve needs a multisampled source) rather than into
        /// a wrong pixel, so it fails loudly on a device and silently in review.
        /// </summary>
        [Fact]
        public void TheSingleSamplePathRecordsNoResolveAtAll()
        {
            using var device = new FakeGpuDevice();
            using var res = new RenderResources(device, W, H, hdrColor: true);
            Assert.False(res.Msaa);

            var cl = new RecordingGpuCommandList(new NullGpuCommandList());
            res.ResolveDepth(cl);
            res.ResolveDepthNormal(cl);
            res.ResolveColor(cl);

            Assert.Empty(cl.Resolves);
        }
    }
}
