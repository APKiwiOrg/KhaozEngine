using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHERE DECISION W2 MEETS DECISIONS W6 AND R3, which is the one place stable framebuffer identity could go
    /// silently wrong and the reason these two tests exist together rather than in either row's own file.
    /// <para>
    /// The swapchain's framebuffer keeps its identity across a resize (W2), and W6's guard in
    /// <see cref="D3D11DeviceState.BindFramebuffer"/> is REFERENCE identity, so a re-bind of that same object
    /// reports no change and issues nothing. On its own that would leave the context pointing at a released render
    /// target view with the viewport still at the old size, which is a black screen with no error anywhere. Two
    /// structural facts make it unreachable: the resize is applied at the PRESENT boundary (W3), so it never lands
    /// inside a recording or a replay, and every replay opens with exactly one <c>ClearState</c> (R3) whose reset
    /// clears the bound framebuffer, so the first bind of the next submit is always a change.
    /// </para>
    /// <para>
    /// Both are asserted here rather than left as two rules that happen to compose, because removing either one
    /// breaks the other's row without failing any of that row's own tests.
    /// </para>
    /// </summary>
    public sealed class D3D11SwapchainRebindTests
    {
        /// <summary>
        /// THE FRAME AFTER A RESIZE RE-BINDS THE RENDER TARGETS AND SETS THE VIEWPORT AT THE NEW SIZE, through
        /// the same framebuffer object it bound at the old one.
        /// </summary>
        [Fact]
        public void TheSubmitAfterAResize_ReBindsTheTargetsAndTheViewportAtTheNewSize()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            IGpuFramebuffer framebuffer = swapchain.Framebuffer;

            // One frame at the old size, then the present boundary the resize is applied at.
            emitter.Begin();
            emitter.SetFramebuffer(framebuffer);
            swapchain.QueueResize(1600, 900);
            swapchain.Present();

            // The next frame, from its ClearState onwards.
            log.Reset();
            emitter.Begin();
            emitter.SetFramebuffer(framebuffer);

            Assert.Equal(
                new[]
                {
                    "ClearState()",
                    $"OMSetRenderTargets({log.Id(framebuffer)})",
                    "RSSetViewports(1,0,0,1600,900,0,1)",
                    "RSSetScissorRects(all:1,0,0,1600,900)",
                },
                log.Trace);
        }

        /// <summary>
        /// THE HAZARD ITSELF, PINNED SO IT CANNOT BE REDISCOVERED THE HARD WAY: without a fresh submit's
        /// <c>ClearState</c> in between, re-binding the swapchain's framebuffer after a resize issues NOTHING,
        /// because W6's guard compares references and the reference did not change.
        /// <para>
        /// This is not a defect and it is not a call to weaken the guard. A resize only ever lands at a present
        /// boundary, and every submit after that boundary opens with the <c>ClearState</c> the test above shows
        /// doing its job. What this asserts is that the <c>ClearState</c> is LOAD-BEARING for the swapchain rather
        /// than merely tidy, so a future change that moves it, makes it conditional or applies a resize somewhere
        /// other than the present boundary fails here with the reason attached.
        /// </para>
        /// </summary>
        [Fact]
        public void WithoutAFreshSubmit_ARebindAfterAResizeIssuesNothing()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            IGpuFramebuffer framebuffer = swapchain.Framebuffer;

            emitter.Begin();
            emitter.SetFramebuffer(framebuffer);
            swapchain.QueueResize(1600, 900);
            swapchain.Present();
            int callsBeforeTheRebind = log.TotalCalls;

            emitter.SetFramebuffer(framebuffer);

            Assert.Equal(callsBeforeTheRebind, log.TotalCalls);
            Assert.Equal(1, log.Count(D3D11NativeCall.RSSetViewports));
            Assert.Equal("RSSetViewports(1,0,0,1280,720,0,1)", log.Trace[^2]);
        }
    }
}
