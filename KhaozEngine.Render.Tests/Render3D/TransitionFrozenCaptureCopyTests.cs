using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/21">#21</see>: which COPY OVERLOAD the frozen
    /// crossfade captures its previous frame with. The capture texture is always single-mip, and
    /// <c>RenderResources.ColorTex</c> carries a full mip chain whenever the final blit is a genuine downscale
    /// (<see cref="Scene3D.WantsMipDownsample"/>: supersampled <see cref="RenderScale.MatchViewport"/>, or an
    /// opted-in <see cref="RenderScale.FixedInternal"/> downscale). A whole-resource <c>CopyTexture</c> names every
    /// subresource on BOTH sides, so across those two mip counts it is not a copy any backend can narrow: the
    /// native Metal and Vulkan backends refuse it from their <c>RequireMatchingShape</c>, and Direct3D 11's
    /// <c>CopyResource</c> wants two identical descriptions. The pass must name mip 0 explicitly.
    ///
    /// <para>Device-free on purpose. Which overload a pass calls, against textures of which shape, is a
    /// source-code fault visible in what the pass records, so it is caught on the ordinary push-path suite rather
    /// than only on a leg with a GPU.</para>
    /// </summary>
    public sealed class TransitionFrozenCaptureCopyTests
    {
        const int W = 64, H = 32;

        /// <summary>One arranged frame: a transition renderer bound to mipped or single-mip targets, licensed to
        /// capture (a frame has resolved), driven through the rising edge of a frozen crossfade.</summary>
        sealed class CaptureHarness : IDisposable
        {
            readonly FakeGpuDevice _device;
            readonly TransitionRenderer _transitions;

            internal CaptureHarness(bool mipped, bool licensed = true)
            {
                _device = new FakeGpuDevice();
                Resources = new RenderResources(_device, W, H, hdrColor: false);
                if (mipped) Resources.Resize(W, H, mipped: true, sampleCount: 1, bloomEnabled: false, hdrColor: false);

                _transitions = new TransitionRenderer(_device,
                    new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
                _transitions.BindTargets(Resources);
                // A full frame has resolved into ColorTex at this size, which is what licenses a capture at all.
                // Withheld by the no-frame-yet case below, where BindTargets has just blanked the targets.
                if (licensed) _transitions.NoteFrameResolved();

                var dissolve = new CameraDissolve();
                dissolve.Begin();
                Assert.True(dissolve.IsActive, "the fixture must drive an ACTIVE transition or it captures nothing");
                Assert.Equal(ScreenTransitionStyle.FrozenCrossfade, dissolve.Style);

                Recorded = new RecordingGpuCommandList(new NullGpuCommandList());
                _transitions.BeginFrame(Recorded, Resources, dissolve);
            }

            internal RenderResources Resources { get; }

            internal RecordingGpuCommandList Recorded { get; }

            public void Dispose()
            {
                Recorded.Dispose();
                _transitions.Dispose();
                Resources.Dispose();
                _device.Dispose();
            }
        }

        /// <summary>
        /// THE DEFECT'S OWN CONFIGURATION: a mipped <c>ColorTex</c>. The capture is one SUBRESOURCE copy of mip 0
        /// at the full target extent, never the whole-resource form, and the two textures genuinely disagree on
        /// mip count (asserted, so the fixture cannot silently stop covering the case it exists for).
        /// </summary>
        [Fact]
        public void AMippedColourTargetIsCapturedAsMipZeroAndNotAsAWholeResourceCopy()
        {
            using var h = new CaptureHarness(mipped: true);
            Assert.True(h.Resources.ColorTex.MipLevels > 1,
                "the fixture must be on the mipped path or it asserts nothing");

            RecordingGpuCommandList.TextureCopy copy = Assert.Single(h.Recorded.TextureCopies);
            Assert.False(copy.WholeResource,
                "a whole-resource copy names every subresource on both sides, and these two do not agree");
            Assert.Same(h.Resources.ColorTex, copy.Source);
            Assert.Equal(0u, copy.SourceMipLevel);
            Assert.Equal(0u, copy.SourceArrayLayer);
            Assert.Equal(0u, copy.DestinationMipLevel);
            Assert.Equal((uint)W, copy.Width);
            Assert.Equal((uint)H, copy.Height);
            Assert.Equal(1u, copy.Destination.MipLevels);
            Assert.NotEqual(copy.Source.MipLevels, copy.Destination.MipLevels);
        }

        /// <summary>
        /// The single-mip configuration takes the SAME path. The two shapes happen to agree there, so a whole copy
        /// would work, and that is exactly why this is pinned: a capture that branched on
        /// <c>RenderResources.Mipped</c> would leave the mipped arm as the only one anyone exercises, which is how
        /// the defect survived unnoticed in the first place.
        /// </summary>
        [Fact]
        public void ASingleMipColourTargetTakesTheSameSubresourcePath()
        {
            using var h = new CaptureHarness(mipped: false);
            Assert.Equal(1u, h.Resources.ColorTex.MipLevels);

            RecordingGpuCommandList.TextureCopy copy = Assert.Single(h.Recorded.TextureCopies);
            Assert.False(copy.WholeResource);
            Assert.Same(h.Resources.ColorTex, copy.Source);
            Assert.Equal(0u, copy.SourceMipLevel);
            Assert.Equal((uint)W, copy.Width);
            Assert.Equal((uint)H, copy.Height);
        }

        /// <summary>
        /// NO RESOLVED FRAME, NO COPY. A crossfade beginning the first frame after a (re)allocation has nothing but
        /// a blank <c>ColorTex</c> to snapshot, so the pass records no copy at all and degrades to a plain fade.
        /// Pinned alongside the two above because it is the arm that proves the capture is gated rather than
        /// unconditional.
        /// </summary>
        [Fact]
        public void ACrossfadeWithNoResolvedFrameYetRecordsNoCopy()
        {
            using var h = new CaptureHarness(mipped: true, licensed: false);
            Assert.Empty(h.Recorded.TextureCopies);
        }
    }
}
