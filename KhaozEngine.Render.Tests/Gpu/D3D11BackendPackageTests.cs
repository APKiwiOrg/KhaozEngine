using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <c>KhaozEngine.Gpu.D3D11</c> package skeleton: its platform guard, its machine-capability probe, and
    /// the state its two creation entry points are in while the device is still being built (work-breakdown row 4
    /// of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>).
    /// <para>
    /// Every test here runs on macOS and Linux as well as Windows, which is the whole point of decision P1: the
    /// package targets <c>net10.0</c> rather than <c>net10.0-windows</c>, so this test class is compiled and run
    /// on every CI leg rather than only the one that has a Direct3D device. Anything whose answer legitimately
    /// differs by platform is asserted through <see cref="KhaozEngineD3D11.IsPlatformSupported"/> rather than
    /// skipped, so the off-Windows behaviour is pinned instead of merely untested.
    /// </para>
    /// </summary>
    public sealed class D3D11BackendPackageTests
    {
        /// <summary>
        /// The guard is the operating system and nothing else. Stated as its own test because everything in the
        /// package hangs off it: it is what keeps the Vortice interop off the load path on macOS and Linux, and
        /// what the platform-compatibility analyzer reads to let the Direct3D bodies carry
        /// <c>[SupportedOSPlatform("windows")]</c> with a single check.
        /// </summary>
        [Fact]
        public void IsPlatformSupported_IsWindowsAndNothingElse()
            => Assert.Equal(OperatingSystem.IsWindows(), KhaozEngineD3D11.IsPlatformSupported);

        /// <summary>
        /// The probe must never throw, on any machine, because "we could not even ask" and "no" are the same
        /// answer to the settings screen and the fallback that consume it. Off Windows the answer is a flat
        /// false, reached without naming a Direct3D type at all.
        /// </summary>
        [Fact]
        public void TheSupportProbe_NeverThrows_AndIsFalseOffWindows()
        {
            var provider = new D3D11BackendProvider();

            bool supported = provider.IsSupported();

            if (!KhaozEngineD3D11.IsPlatformSupported) Assert.False(supported);
            // Asking twice must not change the answer. The selector caches per backend, so a probe that answered
            // differently on a second call would make the cached value depend on who asked first.
            Assert.Equal(supported, provider.IsSupported());
        }

        /// <summary>
        /// CREATION IS BUILT NOW, so what this asserts changed with the device row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/497): off Windows both entry points refuse with a
        /// PLATFORM answer rather than with the old "still being built" one. It is asserted rather than left
        /// implicit because of what the creation path does with the exception: it catches it, WARNs with the
        /// message and falls back to the incumbent, so this text is what a tester who named the native backend
        /// actually reads, and it must name the operating system rather than leaving them to conclude their
        /// machine is at fault. The full transition, including the retired wording, is in
        /// <c>D3D11DeviceWiringTests</c>.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OffWindows_CreationThrows_SayingThePlatformHasNoDirect3D(bool windowed)
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows creation is real

            var provider = new D3D11BackendProvider();

            NotSupportedException ex = windowed
                ? Assert.Throws<PlatformNotSupportedException>(() => provider.CreateForWindow(default))
                : Assert.Throws<PlatformNotSupportedException>(() => provider.CreateHeadless());

            Assert.Contains("Direct3D 11", ex.Message, StringComparison.Ordinal);
            // It must not read as the OTHER failure mode. A missing registration is a wiring fault with its own
            // exception type and its own message, and decision I2 exists to keep the two tellable apart.
            Assert.IsNotType<GpuBackendProviderMissingException>(ex);
        }

        /// <summary>
        /// The provider returns a real device or throws, and never hands back an empty result. Pinned here
        /// because the adopting path checks for a null device and produces its own message for it, and that guard
        /// only stays meaningful while no provider actually relies on it.
        /// <para>
        /// The case driven here is the one that refuses on EVERY operating system: a window handle from a
        /// platform whose windows a Direct3D swapchain cannot present into. Off Windows the platform guard
        /// answers first, and on Windows the handle kind does, so both are a throw rather than an empty result
        /// and neither creates a device inside a plain unit test.
        /// </para>
        /// </summary>
        [Fact]
        public void CreationNeverReturnsAnEmptyResult()
        {
            var provider = new D3D11BackendProvider();

            // default is a Cocoa handle, which no Direct3D swapchain can present into.
            Assert.ThrowsAny<Exception>(() => provider.CreateForWindow(default));
            if (!KhaozEngineD3D11.IsPlatformSupported)
                Assert.ThrowsAny<Exception>(() => provider.CreateHeadless());
        }
    }

    /// <summary>
    /// That the test process actually has the REAL native provider registered, through the one
    /// <c>[ModuleInitializer]</c> section 4.1 allows
    /// (<c>KhaozEngine.TestSupport.Gpu/D3D11BackendRegistration.cs</c>, which every assembly taking
    /// <c>[GpuFact]</c> loads).
    /// <para>
    /// In the non-parallel collection because it reads the process-wide registry, which the append-audit rows
    /// temporarily empty to pin the unregistered behaviour. Worth asserting at all because the registration is
    /// invisible: nothing else in the suite fails if the initializer silently stops running, and the tests that
    /// would notice are exactly the ones that unregister it first.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class D3D11BackendRegistrationTests
    {
        [Fact]
        public void TheTestAssembly_RegistersTheRealNativeBackend()
        {
            Assert.True(GpuBackendProviders.IsRegistered(GpuBackendKind.Direct3D11Native));

            IGpuBackendProvider provider = GpuBackendProviders.Require(GpuBackendKind.Direct3D11Native);
            Assert.Same(typeof(KhaozEngineD3D11).Assembly, provider.GetType().Assembly);
        }

        /// <summary>
        /// A repeated startup call is harmless, which matters because "call it once at startup" is advice rather
        /// than something the type system enforces, and a game with two entry points can easily call it twice.
        /// </summary>
        [Fact]
        public void Register_IsIdempotent()
        {
            IGpuBackendProvider first = GpuBackendProviders.Require(GpuBackendKind.Direct3D11Native);

            KhaozEngineD3D11.Register();
            KhaozEngineD3D11.Register();

            Assert.Same(first, GpuBackendProviders.Require(GpuBackendKind.Direct3D11Native));
        }

        /// <summary>
        /// THE CLAIM DECISION P1 RESTS ON, checked rather than reasoned about: on a platform that is not Windows,
        /// registering the backend and asking it whether this machine is supported must not put the Direct3D
        /// interop assembly into the process. That is what makes a <c>net10.0</c> package safe to reference
        /// unconditionally from every consumer and from this test project, and it holds only while every body
        /// naming a Vortice type stays <c>NoInlining</c> behind the platform guard.
        /// <para>
        /// A failure here is not a style point. The JIT resolves a method's types when it compiles that method,
        /// so an inlined or unguarded body means a macOS or Linux run loads a Windows-only native binding, and
        /// what the user sees is a startup crash naming an assembly they never asked for. The assertion itself
        /// lives on <see cref="D3D11InteropLoad"/>, shared with the fence subsystem's load-path test.
        /// </para>
        /// </summary>
        [Fact]
        public void OffWindows_NothingInThisPackagePullsInTheDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            KhaozEngineD3D11.Register();
            Assert.False(GpuBackendProviders.Require(GpuBackendKind.Direct3D11Native).IsSupported());

            D3D11InteropLoad.AssertNotLoaded();
        }
    }
}
