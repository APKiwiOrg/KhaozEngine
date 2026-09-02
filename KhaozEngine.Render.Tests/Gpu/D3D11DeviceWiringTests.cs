using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE ROW'S WIRING, AS FAR AS IT IS CHECKABLE WITHOUT A DIRECT3D DEVICE
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/497). Construction itself is Windows-only end to end, so
    /// what runs here is the two things that are not: the provider's refusal off Windows, and the SHAPE of the
    /// construction and teardown that a real device performs.
    ///
    /// <para><b>THE SHAPE IS READ OUT OF THE ASSEMBLY FILE, not off loaded types, and that is deliberate rather
    /// than exotic.</b> <c>D3D11GpuDevice</c> is Windows-only and holds Direct3D references, so a reflection walk
    /// that touched its field types or resolved its call targets would load the Vortice interop on macOS, and the
    /// suite asserts process-wide that nothing does (<c>D3D11InteropLoad</c>). A
    /// <see cref="MetadataReader"/> over the compiled file answers the same questions and loads nothing at all:
    /// which types the constructor instantiates and in what order, which members the teardown calls and in what
    /// order, and which fields the type declares.</para>
    ///
    /// <para><b>THE READER ITSELF IS <see cref="D3D11BackendMetadata"/></b>, which is where the scan's limits
    /// are written down. What matters here is that its failure direction is a false ALARM rather than a false
    /// pass, and that the assertions below are about ORDER, which a genuine call sequence satisfies.</para>
    /// </summary>
    public sealed class D3D11DeviceWiringTests
    {
        const string DeviceType = "D3D11GpuDevice";

        // ---------------------------------------------------------------------------------------------------
        // The provider's throw-to-real transition.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE OLD "NOT BUILT YET" ANSWER IS GONE, and off Windows what replaces it is a PLATFORM answer. This is
        /// the exception a caller who named the native backend on macOS or Linux actually reads, and it must not
        /// say the backend is unfinished (it is not) or that their machine is at fault in some unnamed way.
        /// <para>
        /// It must also reach that answer WITHOUT loading the Direct3D interop, which is the whole reason the
        /// platform guard is the first statement of both entry points rather than a check inside the creation
        /// body.
        /// </para>
        /// </summary>
        [Fact]
        public void OffWindows_BothCreationEntryPoints_RefuseWithAPlatformAnswerAndLoadNoInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows they create a real device

            var provider = new D3D11BackendProvider();

            var windowed = Assert.Throws<PlatformNotSupportedException>(
                () => provider.CreateForWindow(new GpuWindowedDeviceRequest(
                    new GpuWindowHandle(GpuWindowKind.Win32, new IntPtr(1)), 640, 480, true)));
            var headless = Assert.Throws<PlatformNotSupportedException>(() => provider.CreateHeadless());

            foreach (Exception ex in new Exception[] { windowed, headless })
            {
                Assert.Contains("Direct3D 11", ex.Message, StringComparison.Ordinal);
                Assert.Contains("operating system", ex.Message, StringComparison.Ordinal);
                // The retired message. A build that still said this would be telling a tester the row never
                // landed, which is the one thing this exception must no longer claim.
                Assert.DoesNotContain("still being built", ex.Message, StringComparison.Ordinal);
            }

            D3D11InteropLoad.AssertNotLoaded();
        }

        // ---------------------------------------------------------------------------------------------------
        // Issue #476: the device constructs exactly ONE state object and ONE emitter context.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE REMAINING HALF OF https://github.com/APKiwiOrg/KhaozEngine/issues/476, and the reason that issue
        /// stayed open after the replay row: the mechanical check there proves every emitter implementation
        /// RECEIVES a <c>D3D11DeviceState</c>, and this proves the DEVICE is the only thing that makes one.
        /// <para>
        /// Both halves are needed and neither implies the other. A readonly struct that allocated its own state
        /// in its constructor would satisfy the first and reintroduce exactly the defect: list B binds pipeline
        /// P, list A's copy still believes A's pipeline is current, A skips the rebind and draws with B's state,
        /// with nothing thrown and nothing logged. What makes that impossible is that there is ONE state in the
        /// process per device, and one construction site is how that is enforced.
        /// </para>
        /// </summary>
        [Fact]
        public void TheDeviceIsTheOnlyThingInTheBackendThatConstructsAStateOrAnEmitterContext()
        {
            using var backend = D3D11BackendMetadata.Open();

            AssertSoleConstructionSite(backend, "D3D11DeviceState");
            AssertSoleConstructionSite(backend, "D3D11EmitterContext");
            AssertSoleConstructionSite(backend, "D3D11NativeEmitter");
        }

        /// <summary>
        /// The other half of "one per device": the device holds exactly one FIELD of each, so there is no second
        /// slot a later edit could park a per-list copy in. Read off the field signatures rather than off loaded
        /// <c>FieldInfo.FieldType</c>s, which would resolve the Direct3D fields beside them.
        /// </summary>
        [Fact]
        public void TheDeviceDeclaresExactlyOneStateOneEmitterContextAndOneEmitter()
        {
            using var backend = D3D11BackendMetadata.Open();
            IReadOnlyList<string> fields = backend.FieldTypeNames(DeviceType);

            Assert.Equal(1, fields.Count(f => f == "D3D11DeviceState"));
            Assert.Equal(1, fields.Count(f => f == "D3D11EmitterContext"));
            Assert.Equal(1, fields.Count(f => f == "D3D11NativeEmitter"));
            // One ring, one fence subsystem and one loss latch, for the same reason: every one of them carries
            // state the whole device has to agree about (a segment's owner, the timeline's value, whether the
            // device is lost), and a second instance would be a second answer.
            Assert.Equal(1, fields.Count(f => f == "D3D11RingAllocator"));
            Assert.Equal(1, fields.Count(f => f == "D3D11FenceSubsystem"));
            Assert.Equal(1, fields.Count(f => f == "D3D11DeviceLossLatch"));
        }

        // ---------------------------------------------------------------------------------------------------
        // The construction order, and the teardown order.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE CONSTRUCTION ORDER OF ISSUE #497, as the constructor actually performs it. Every step here is a
        /// dependency of the one after it, which is why the order is worth pinning at all rather than merely
        /// writing down: the ring reads the fence subsystem's completion value, the bind flush the state composes
        /// takes the ring, the factory validates against the capabilities, and the swapchain and the staging path
        /// both take the liveness token and the latch that were built first.
        /// <para>
        /// TWO STEPS RUN EARLIER THAN THE ISSUE LISTS THEM, and both are forced: the capability read is before
        /// the factory because the factory takes the capabilities, and the one device state is after the ring
        /// because the state composes the bind flush and the bind flush takes the ring. Neither changes what is
        /// built.
        /// </para>
        /// </summary>
        [Fact]
        public void TheConstructorBuildsEverySubsystemInDependencyOrder()
        {
            using var backend = D3D11BackendMetadata.Open();

            IReadOnlyList<string> built = backend.ConstructedTypesIn(DeviceType, ".ctor", new[]
            {
                "DeviceLiveness", "D3D11DeviceLossLatch", "D3D11FenceSubsystem", "D3D11RingAllocator",
                "D3D11DeviceState", "D3D11EmitterContext", "D3D11NativeEmitter", "D3D11ResourceFactory",
                "D3D11StagingAccess", "D3D11Swapchain",
            });

            Assert.Equal(new[]
            {
                "DeviceLiveness", "D3D11DeviceLossLatch", "D3D11FenceSubsystem", "D3D11RingAllocator",
                "D3D11DeviceState", "D3D11EmitterContext", "D3D11NativeEmitter", "D3D11ResourceFactory",
                "D3D11StagingAccess", "D3D11Swapchain",
            }, built);
        }

        /// <summary>
        /// A CONSTRUCTION THAT THROWS PART WAY RELEASES WHAT IT ALREADY BUILT
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/503). Every subsystem above that owns a native object
        /// holds a COM reference count on the <c>ID3D11Device</c>, so orphaning one kept the whole device and
        /// every driver allocation behind it alive until the process exited, beside the fallback session created
        /// after the failure.
        /// <para>
        /// WHAT THIS PINS IS THE WIRING, not the rule. The unwind rule itself is
        /// <see cref="D3D11ConstructionScopeTests"/>, driven with fakes that throw from a chosen step. This is
        /// the other half: that the SHIPPED constructor registers its steps, commits on success, and unwinds on
        /// the way out. Nothing else can check it, because the constructor is Windows-only end to end.
        /// </para>
        /// </summary>
        [Fact]
        public void AConstructionThatThrowsPartWay_ReleasesWhatItAlreadyBuilt()
        {
            using var backend = D3D11BackendMetadata.Open();

            Assert.Contains("D3D11ConstructionScope",
                backend.ConstructedTypesIn(DeviceType, ".ctor", new[] { "D3D11ConstructionScope" }));

            IReadOnlyList<string> calls = backend.CalledMembersIn(DeviceType, ".ctor", new[]
            {
                "D3D11ConstructionScope.Track", "D3D11ConstructionScope.TrackRelease",
                "D3D11ConstructionScope.Commit", "D3D11ConstructionScope.Unwind",
            });

            // Register, then commit, then unwind. The unwind is LAST in IL because it is in the catch handler,
            // which is exactly where it has to be: it must not run on the path that succeeded. TrackRelease sits
            // beside Track because the shared sampler pair is non-owning and only its own destroy frees it
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/506).
            Assert.Equal(new[]
            {
                "D3D11ConstructionScope.Track", "D3D11ConstructionScope.TrackRelease",
                "D3D11ConstructionScope.Commit", "D3D11ConstructionScope.Unwind",
            }, calls);
        }

        /// <summary>
        /// EVERY STEP THAT OWNS A NATIVE OBJECT IS REGISTERED, and this is the assertion that goes red when a
        /// tenth subsystem lands without one. Five of the nine own something: the fence subsystem (which carries
        /// the timeline's fence or its query pool), the two shared samplers, the swapchain and its views, and the
        /// info-queue pump. The other four hold only managed state and have nothing to release.
        /// </summary>
        [Fact]
        public void EverySubsystemThatOwnsANativeObject_IsRegisteredWhileItIsBuilt()
        {
            using var backend = D3D11BackendMetadata.Open();

            int registered = backend.CallCountIn(DeviceType, ".ctor", "D3D11ConstructionScope.Track")
                + backend.CallCountIn(DeviceType, ".ctor", "D3D11ConstructionScope.TrackRelease");

            Assert.Equal(5, registered);
        }

        /// <summary>
        /// THE TEARDOWN ORDER, which is the half of the wiring that only fails at shutdown and therefore the half
        /// nobody sees fail. Three clauses, and each one is a real hazard rather than tidiness:
        /// <list type="number">
        ///   <item><description>The DRAIN comes first, while the device is still live and while nothing holds the
        ///   submit lock. It refuses a caller holding that lock by name, and it is the one member here that can
        ///   block. It is the BOUNDED entry point, because this method runs inside the process-wide device
        ///   lifecycle gate (https://github.com/APKiwiOrg/KhaozEngine/issues/505), and reading that off the
        ///   assembly is what stops a later edit quietly putting the unbounded one back.</description></item>
        ///   <item><description>The releases come next, in the order that leaves nothing referenced: the debug
        ///   pump, the swapchain and its views, then the fence subsystem, which takes the timeline's fence and
        ///   event objects with it.</description></item>
        ///   <item><description>The liveness token is flipped LAST. Every release above reads it and no-ops when
        ///   it says dead, so flipping it first (which is what the Veldrid wrapper did, correctly, because
        ///   destroying a Veldrid device freed its children) would silently skip all of them and leave the
        ///   ID3D11Device alive holding a swapchain nobody can reach.</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public void TeardownDrainsFirstReleasesNextAndFlipsLivenessLast()
        {
            using var backend = D3D11BackendMetadata.Open();

            IReadOnlyList<string> calls = backend.CalledMembersIn(DeviceType, "MarkDeviceDisposed", new[]
            {
                "D3D11FenceSubsystem.WaitForIdle", "D3D11FenceSubsystem.WaitForIdleAtTeardown",
                "D3D11InfoQueuePump.Dispose", "D3D11Swapchain.Dispose", "D3D11Sampler.DestroyShared",
                "D3D11FenceSubsystem.Dispose", "DeviceLiveness.MarkDead",
            });

            // WaitForIdle is in the asked-about set and absent from the answer, which is the assertion that the
            // teardown takes the bounded drain rather than the frame path's.
            Assert.Equal(new[]
            {
                "D3D11FenceSubsystem.WaitForIdleAtTeardown", "D3D11InfoQueuePump.Dispose",
                "D3D11Swapchain.Dispose", "D3D11Sampler.DestroyShared", "D3D11FenceSubsystem.Dispose",
                "DeviceLiveness.MarkDead",
            }, calls);
        }

        /// <summary>
        /// The present boundary calls the two frame-boundary members that REFUSE a caller holding the submit
        /// lock, so it must call them after the swapchain's present has released it. Pinned by absence: the
        /// present body takes no lock of its own at all, which is what makes the two calls provably outside it.
        /// <para>
        /// The ring's <c>BeginFrame</c> is the one that matters, because it waits for the GPU to finish with the
        /// segment it opens, which is up to a frame. Inside the submit lock that is a frame-long hold of the lock
        /// decision W4 caps at microseconds, and on the event-query fence mechanism it also shuts out the
        /// submission that would end the wait.
        /// </para>
        /// </summary>
        [Fact]
        public void ThePresentBoundaryRollsTheFrameCountersOutsideTheSubmitLock()
        {
            using var backend = D3D11BackendMetadata.Open();

            IReadOnlyList<string> calls = backend.CalledMembersIn(DeviceType, "Present", new[]
            {
                "D3D11Swapchain.Present", "D3D11DeviceLossLatch.Check", "D3D11FenceSubsystem.BeginFrame",
                "D3D11RingAllocator.BeginFrame", "Monitor.Enter",
            });

            Assert.Equal(new[]
            {
                "D3D11Swapchain.Present", "D3D11DeviceLossLatch.Check", "D3D11FenceSubsystem.BeginFrame",
                "D3D11RingAllocator.BeginFrame",
            }, calls);
        }

        // ---------------------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------------------

        static void AssertSoleConstructionSite(D3D11BackendMetadata backend, string typeName)
        {
            IReadOnlyList<string> sites = backend.ConstructionSitesOf(typeName);

            Assert.Equal(new[] { $"{DeviceType}..ctor" }, sites);
        }
    }
}
