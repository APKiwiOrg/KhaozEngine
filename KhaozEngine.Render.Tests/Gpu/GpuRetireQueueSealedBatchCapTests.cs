using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SAFETY-VALVE CAP SIZED OFF THE RUNNING BACKEND'S FRAMES-IN-FLIGHT KNOB, and reaching a
    /// <c>Scene3D</c> without a consumer doing anything
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/661">#661</see>).
    ///
    /// <para><b>THE THING THAT WAS BROKEN WAS THE ADVICE, NOT THE VALVE.</b> Three separate docs told a consumer
    /// to raise <c>MaxSealedBatches</c> alongside <c>KE_*_FRAMES_IN_FLIGHT</c>, and there was no way to do it:
    /// the parameter is on <c>GpuRetireQueue.Create</c>, <c>Scene3D</c> called that bare, its ctor is internal,
    /// and the three public routes into a scene take a <c>ShadowSettings</c> and nothing else. So the cost of a
    /// deepened pipeline was a valve drain per nine frames that nobody could avoid.</para>
    ///
    /// <para><b>THE FIX IS AUTOMATIC RATHER THAN PLUMBED, which is a choice.</b> Threading an option down from
    /// <c>Render3DSurface</c>, <c>Render3DPreview</c> and <c>Render3DSnapshot</c> would have made the advice
    /// followable and left it manual, and a consumer who has already set an env var should not have to know a
    /// second knob exists to keep it from costing them. The scene reads the knob it is already being paced by.</para>
    ///
    /// <para><b>THE ENV-VAR NAMES ARE RESTATED IN <c>KhaozEngine.Gpu</c> AND THE AGREEMENT IS ASSERTED HERE.</b>
    /// Each backend's constant lives in that backend's own package and the seam cannot reference any of them, so
    /// the duplicate is unavoidable and the drift is not: the row below compares the reader's name and bounds
    /// against all three backends' own constants, so a rename goes red rather than sizing the cap off a variable
    /// nobody sets any more.</para>
    ///
    /// <para><b>PROCESS-GLOBAL STATE:</b> the two rows that raise a knob write a real backend's environment
    /// variable, which every device creation in the process reads, so the class enlists in
    /// <c>NativeDeviceLifecycle</c> beside everything else that builds a device.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class GpuRetireQueueSealedBatchCapTests
    {
        /// <summary>
        /// THE DEFAULT DEPTH LANDS EXACTLY ON THE DEFAULT CAP, which is what makes this change byte-identical for
        /// every consumer who has not touched a knob. Everything else here is only interesting because this holds.
        /// </summary>
        [Fact]
        public void TheShippedDepthResolvesToTheShippedCap()
        {
            Assert.Equal(GpuRetireQueue.DefaultMaxSealedBatches,
                GpuRetireQueue.SealedBatchCapForDepth(GpuRetireQueue.DefaultFramesInFlight));
        }

        /// <summary>
        /// A RAISED DEPTH RAISES THE CAP BY THE SAME AMOUNT, so the margin the shipped default clears the shipped
        /// depth by is preserved rather than a ratio being reapplied.
        /// </summary>
        [Theory]
        [InlineData(4, 9)]
        [InlineData(6, 11)]
        [InlineData(8, 13)]
        [InlineData(GpuRetireQueue.MaximumFramesInFlight, 21)]
        public void ARaisedDepthRaisesTheCapByTheSameAmount(int framesInFlight, int expectedCap)
            => Assert.Equal(expectedCap, GpuRetireQueue.SealedBatchCapForDepth(framesInFlight));

        /// <summary>
        /// AND A LOWERED DEPTH DOES NOT LOWER IT. A smaller cap buys nothing (the holding is bounded by what the
        /// loop retires) and would cost a valve drain on an UNTHROTTLED loop, which is the case the bound exists
        /// for and which the depth knob says nothing about.
        /// </summary>
        [Theory]
        [InlineData(GpuRetireQueue.MinimumFramesInFlight)]
        [InlineData(2)]
        public void ALoweredDepthKeepsTheDefaultCap(int framesInFlight)
            => Assert.Equal(GpuRetireQueue.DefaultMaxSealedBatches,
                GpuRetireQueue.SealedBatchCapForDepth(framesInFlight));

        /// <summary>
        /// THE PARSE REFUSES EXACTLY WHAT THE BACKENDS REFUSE, because a value the backend replaces with its own
        /// default would otherwise size a cap for a depth that is not in force. Unset, blank, unparseable, and
        /// out of range on either side all come back as the shipped depth.
        /// </summary>
        [Theory]
        [InlineData(null, GpuRetireQueue.DefaultFramesInFlight)]
        [InlineData("", GpuRetireQueue.DefaultFramesInFlight)]
        [InlineData("   ", GpuRetireQueue.DefaultFramesInFlight)]
        [InlineData("six", GpuRetireQueue.DefaultFramesInFlight)]
        [InlineData("0", GpuRetireQueue.DefaultFramesInFlight)]
        [InlineData("17", GpuRetireQueue.DefaultFramesInFlight)]
        [InlineData("6", 6)]
        [InlineData(" 6 ", 6)]
        [InlineData("16", GpuRetireQueue.MaximumFramesInFlight)]
        public void TheDepthParseRefusesWhatTheBackendsRefuse(string? envValue, int expected)
            => Assert.Equal(expected, GpuRetireQueue.ResolveFramesInFlight(envValue));

        /// <summary>
        /// THE DUPLICATE-KILLER. The seam restates each backend's env-var name and bounds because it cannot
        /// reference the packages that own them, so this row is the mechanism that keeps the copies honest.
        /// A red run here means one side was renamed or rebounded and the cap is now sized off nothing.
        /// </summary>
        [Fact]
        public void TheEnvVarNamesAndBoundsAgreeWithEveryBackendsOwnConstants()
        {
            Assert.Equal(MetalFramesInFlight.EnvVarName,
                GpuRetireQueue.FramesInFlightEnvVarFor(GpuBackendKind.MetalNative));
            Assert.Equal(VulkanFramesInFlight.EnvVarName,
                GpuRetireQueue.FramesInFlightEnvVarFor(GpuBackendKind.VulkanNative));
            Assert.Equal(D3D11FramesInFlight.EnvVarName,
                GpuRetireQueue.FramesInFlightEnvVarFor(GpuBackendKind.Direct3D11Native));

            // One shipped default across all three, which is the number the shipped cap was chosen against.
            Assert.Equal(GpuRetireQueue.DefaultFramesInFlight, MetalFramesInFlight.Default);
            Assert.Equal(GpuRetireQueue.DefaultFramesInFlight, VulkanFramesInFlight.Default);
            Assert.Equal(GpuRetireQueue.DefaultFramesInFlight, D3D11FramesInFlight.Default);

            // One shipped ceiling too, so the reader refuses the same overshoot the backends do.
            Assert.Equal(GpuRetireQueue.MaximumFramesInFlight, MetalFramesInFlight.Maximum);
            Assert.Equal(GpuRetireQueue.MaximumFramesInFlight, VulkanFramesInFlight.Maximum);
            Assert.Equal(GpuRetireQueue.MaximumFramesInFlight, D3D11FramesInFlight.Maximum);

            // The floor is the LOOSEST of the three, which the class note argues for: Vulkan's own 2 refusing a 1
            // lands on the default cap anyway, so carrying a per-backend floor here would buy no difference.
            Assert.Equal(GpuRetireQueue.MinimumFramesInFlight, MetalFramesInFlight.Minimum);
            Assert.Equal(GpuRetireQueue.MinimumFramesInFlight, D3D11FramesInFlight.Minimum);
            Assert.True(VulkanFramesInFlight.Minimum >= GpuRetireQueue.MinimumFramesInFlight);
        }

        /// <summary>
        /// A BACKEND WITH NO SUCH KNOB TAKES THE DEFAULT, rather than the lookup throwing on a kind it does not
        /// know. Every retired member and any appended one lands here until it says otherwise.
        /// </summary>
        [Fact]
        public void ABackendWithNoKnobTakesTheDefaultCap()
        {
            Assert.Null(GpuRetireQueue.FramesInFlightEnvVarFor(GpuBackendKind.OpenGL));

            var device = new FakeGpuDevice(GpuBackendKind.OpenGL);
            Assert.Equal(GpuRetireQueue.DefaultMaxSealedBatches, GpuRetireQueue.SealedBatchCapFor(device));
        }

        /// <summary>
        /// THE READ ITSELF, through a device that names a real backend. The env var is the running process's, so
        /// this and the scene row below are why the class is serialized.
        /// </summary>
        [Fact]
        public void ARaisedKnobRaisesTheCapForADeviceOnThatBackend()
        {
            var device = new FakeGpuDevice(GpuBackendKind.MetalNative);
            Assert.Equal(GpuRetireQueue.DefaultMaxSealedBatches, GpuRetireQueue.SealedBatchCapFor(device));

            using (new EnvScope(MetalFramesInFlight.EnvVarName, "6"))
            {
                Assert.Equal(11, GpuRetireQueue.SealedBatchCapFor(device));
            }

            // And a knob raised for a DIFFERENT backend leaves this one alone.
            using (new EnvScope(VulkanFramesInFlight.EnvVarName, "16"))
            {
                Assert.Equal(GpuRetireQueue.DefaultMaxSealedBatches, GpuRetireQueue.SealedBatchCapFor(device));
            }
        }

        /// <summary>
        /// END TO END, which is the row that actually closes the issue: a scene built through one of the three
        /// public entry points carries the raised cap, with the consumer having set nothing but the depth knob
        /// they already meant to set. The default arm runs first and asserts the byte-identical half.
        /// <para>
        /// It is a <c>[GpuFact]</c> because a <c>Scene3D</c> needs a real device, and the knob it reads is the one
        /// belonging to whichever backend this leg is running, so the row works on every leg rather than pinning
        /// Metal's name.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ASceneSizesItsQueueOffTheRunningBackendsKnob()
        {
            Assert.Equal(GpuRetireQueue.DefaultMaxSealedBatches, CapturedSceneCap());

            using GpuDeviceContext probe = GpuDeviceContext.CreateHeadless();
            string? envVar = GpuRetireQueue.FramesInFlightEnvVarFor(probe.GpuDevice.Backend);
            Assert.NotNull(envVar);

            using (new EnvScope(envVar, "6"))
            {
                Assert.Equal(11, CapturedSceneCap());
            }
        }

        // The cap the scene behind Render3DSnapshot.Capture was built with. One frame, one pixel, nothing drawn:
        // the construction is the whole subject.
        static int CapturedSceneCap()
        {
            int cap = 0;
            Render3DSnapshot.Capture(16, 16,
                setup: scene =>
                {
                    cap = scene.RetireSealedBatchCap;
                    scene.Post.Starfield = false;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
                },
                drawFrame: static _ => { },
                frames: 1);
            return cap;
        }
    }
}
