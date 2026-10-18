using System;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A texture copy applies a pending clear before it opens its blit encoder, device free through a real
    /// <see cref="MetalCommandList"/> over the encoder and render fakes. The copy members themselves take a
    /// <see cref="MetalTexture"/>, which cannot be built without a device (see <see cref="MetalTransferPathTests"/>),
    /// so these rows drive <see cref="MetalCommandList.EnsureCopyEncoder"/>, the one helper every texture copy opens
    /// its encoder through. The pixels are the <c>[GpuFact]</c> companions'
    /// (<c>MetalRenderPassGpuTests.A_copy_after_a_clear_only_pass_reads_the_cleared_texels</c> and
    /// <c>MetalSwapchainGpuTests.A_requested_capture_copies_a_real_drawable</c>).
    /// </summary>
    public sealed class MetalCopyClearOrderTests : IDisposable
    {
        static readonly Color Colour = Color.FromBytes(40, 120, 200, 255);

        readonly MetalRingHarness _harness = new();
        readonly FakeMetalEncoderCalls _calls = new();
        readonly FakeMetalRenderCalls _render = new();
        readonly MetalCommandList _list;

        public MetalCopyClearOrderTests()
            => _list = _harness.NewList(new object(), calls: _calls, render: _render);

        /// <inheritdoc/>
        public void Dispose()
        {
            _list.Dispose();
            _harness.Dispose();
        }

        [Fact]
        public void A_blit_after_a_pending_clear_applies_the_clear_first()
        {
            _list.Begin();
            _list.SetFramebuffer(new RecordedFramebuffer());
            _list.ClearColorTarget(0, Colour);

            IntPtr encoder = _list.EnsureCopyEncoder();

            Assert.NotEqual(IntPtr.Zero, encoder);
            Assert.Equal(MetalEncoderKind.Blit, _list.Encoders.Open);

            // THE CLEAR RAN AS AN EMPTY PASS WITH THE CLEAR LOAD ACTION, and before the blit began.
            RecordedRenderPass pass = Assert.Single(_render.Passes);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[0].LoadAction);
            Assert.Equal(Colour, pass.Colour[0].ClearValue);

            Assert.Equal(3, _calls.Log.Count);
            Assert.StartsWith("begin Render", _calls.Log[0], StringComparison.Ordinal);
            Assert.StartsWith("end Render", _calls.Log[1], StringComparison.Ordinal);
            Assert.StartsWith("begin Blit", _calls.Log[2], StringComparison.Ordinal);

            // AND IT IS NOT OWED AGAIN AT End, which would be a second clear over the copy's destination.
            _list.End();
            Assert.Single(_render.Passes);
        }

        [Fact]
        public void A_blit_with_no_pending_clear_opens_no_render_pass()
        {
            _list.Begin();
            _list.SetFramebuffer(new RecordedFramebuffer());

            _list.EnsureCopyEncoder();

            Assert.Empty(_render.Passes);
            Assert.StartsWith("begin Blit", Assert.Single(_calls.Log), StringComparison.Ordinal);
        }
    }
}
