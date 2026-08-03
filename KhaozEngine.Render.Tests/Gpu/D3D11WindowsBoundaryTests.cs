using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE CLAIM DECISION P1 RESTS ON, checked for the fence subsystem: the package targets <c>net10.0</c>
    /// instead of <c>net10.0-windows</c>, so it is referenced unconditionally by every consumer and by this test
    /// project, and the ONLY thing keeping the Direct3D interop off the load path on macOS and Linux is that
    /// every body naming a Vortice type is <c>NoInlining</c> behind
    /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/>.
    /// <para>
    /// A failure here is not a style point. The JIT resolves a method's types when it compiles that method, so an
    /// inlined or unguarded body means a macOS or Linux run loads a Windows-only native binding, and what the
    /// user sees is a startup crash naming an assembly they never asked for.
    /// </para>
    ///
    /// <para><b>DO NOT ADD A REFLECTION SCAN HERE, and this is the note that saves the next reader the hour it
    /// cost to find out.</b> The obvious complement to these tests is a sweep over the backend assembly asserting
    /// that every method whose signature names a Vortice type carries the two attributes. That sweep cannot
    /// exist. Reading <c>ParameterInfo.ParameterType</c> or <c>MethodInfo.ReturnType</c> RESOLVES the type, which
    /// loads <c>Vortice.Direct3D11</c>, <c>Vortice.DirectX</c> and <c>SharpGen.Runtime</c> into the process, and
    /// the assertion below is about exactly that list being empty. The scan would pass while making the thing it
    /// was written to protect impossible to observe, and it would take the landed registration-path test
    /// (<c>D3D11BackendRegistrationTests</c>) down with it, since the load is process-wide and permanent. A check
    /// of that shape needs a <c>MetadataLoadContext</c> or a raw <c>MetadataReader</c>, which resolve nothing
    /// into the running process, and neither is worth a package reference on this row.</para>
    ///
    /// <para>Both tests assert a PROCESS-WIDE fact, so they hold only while nothing else in the suite loads the
    /// interop. That is the same standing condition the landed registration-path test has, and it is what makes
    /// the paragraph above binding rather than advisory.</para>
    /// </summary>
    public sealed class D3D11WindowsBoundaryTests
    {
        /// <summary>
        /// The whole engine-facing fence surface, driven end to end off Windows: create the subsystem over a
        /// device-free timeline, create fences, arm them through the replay-tail signal, poll them, drain, roll a
        /// frame of telemetry, kill the device and tear down. None of that may put Vortice in the process.
        /// <para>
        /// This is the fence subsystem's version of the registration path's load-path test, and it is the one
        /// that matters day to day: these members are what the device will call every frame, so they are where a
        /// future edit is most likely to reach a Direct3D type from, and every one of them is on the far side of
        /// the timeline interface precisely so it does not have to.
        /// </para>
        /// </summary>
        [Fact]
        public void OffWindows_TheWholeFenceSurfaceRunsWithoutLoadingTheDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            var liveness = new FakeD3D11DeviceLiveness();
            using (var fences = new D3D11FenceSubsystem(
                timeline, new object(), liveness, D3D11RealDrain.Resolve(null, out _)))
            {
                Assert.True(fences.SupportsCompletionFences);
                IGpuFence fence = fences.CreateFence();
                fences.SignalEndOfReplay(fence);
                fences.SignalEndOfReplay(null);
                _ = fence.Signaled;
                fence.Reset();
                _ = fences.CompletedValue;
                fences.WaitForIdle();
                fences.BeginFrame();
                _ = fences.LastFrameDrain;
                _ = fences.Mechanism;
                _ = fences.RealDrainEnabled;
                liveness.IsDead = true;
                _ = fence.Signaled;
                fences.WaitForIdle();
                fence.Dispose();
            }

            AssertNoInteropLoaded();
        }

        /// <summary>
        /// The environment levers and the guard message are readable off Windows too, and asking for them loads
        /// nothing. The guard branch itself is unreachable by construction (both timeline constructors are
        /// Windows-only), which is exactly why the message is worth pinning: nothing else would notice if it
        /// stopped being there, and it is what the platform-compatibility analyzer reads to let the Direct3D
        /// calls below it stay guarded by one check.
        /// </summary>
        [Fact]
        public void OffWindows_TheGuardMessageAndTheEnvironmentLeversLoadNothing()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;

            PlatformNotSupportedException ex = D3D11PlatformGuard.NotOnThisPlatform("fence timeline");
            Assert.Contains("Direct3D 11", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(KhaozEngineD3D11.IsPlatformSupported), ex.Message, StringComparison.Ordinal);

            // Reading the live environment is the one impure member on the kill switch, and it has to work
            // everywhere: the value it returns depends on the machine, so what is asserted is that asking is
            // legal off Windows and loads nothing, not what the answer was.
            _ = D3D11RealDrain.FromEnvironment(out _);
            Assert.Contains(D3D11RealDrain.EnvVarName, D3D11RealDrain.DisabledDescription, StringComparison.Ordinal);

            AssertNoInteropLoaded();
        }

        static void AssertNoInteropLoaded()
        {
            string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name ?? "")
                .Where(n => n.StartsWith("Vortice", StringComparison.Ordinal)
                    || n.StartsWith("SharpGen", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.True(loaded.Length == 0,
                "The Direct3D interop was loaded on a platform that has none: [" + string.Join(", ", loaded) +
                "]. Either some body that names a Vortice type is no longer NoInlining behind "
                + "KhaozEngineD3D11.IsPlatformSupported, or something in this suite resolved a Vortice type "
                + "through reflection. See the note on this class for why the second one is not a false alarm to "
                + "be worked around.");
        }
    }
}
