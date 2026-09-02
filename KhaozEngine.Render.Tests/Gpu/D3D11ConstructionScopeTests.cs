using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A NATIVE DEVICE THAT FAILS PART WAY THROUGH CONSTRUCTION RELEASES WHAT IT ALREADY BUILT
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/503).
    ///
    /// <para><b>WHY THE LEAK WAS THE WHOLE DEVICE AND NOT A HANDFUL OF OBJECTS.</b> Each subsystem the
    /// constructor builds holds a COM reference count on the <c>ID3D11Device</c>. The creation path's own
    /// <c>finally</c> drops exactly one of those, so orphaning any child kept the native device, and every driver
    /// allocation behind it, alive for the life of the process. The reachable trigger is the swapchain, which
    /// DXGI refuses for a window handle or a display in a bad state on a path the device context CATCHES: the
    /// observable symptom was a session that fell back with a fully allocated orphan device beside it.</para>
    ///
    /// <para><b>DEVICE-FREE, WHICH IS WHY THE FIX IS SHAPED THIS WAY.</b> The constructor is Windows-only end to
    /// end and no line of it runs here. The RULE is not: everything already built is released, newest first,
    /// nothing built later is touched, and a release that throws does not stop the ones after it. Those are
    /// driven below with fakes that throw from a chosen step. That the shipped constructor actually routes
    /// through this is the other half, pinned off the assembly by
    /// <c>D3D11DeviceWiringTests.AConstructionThatThrowsPartWayReleasesWhatItAlreadyBuilt</c>.</para>
    /// </summary>
    public sealed class D3D11ConstructionScopeTests
    {
        /// <summary>
        /// THE ROW THE ISSUE ASKS FOR: five steps, the fourth throws, and the three before it are released newest
        /// first while the fifth is never reached. Newest first is the order the real teardown takes for the same
        /// reason a constructor's order is a dependency order: a subsystem may hold the one built before it.
        /// </summary>
        [Fact]
        public void AThrowFromOneStep_ReleasesEveryEarlierStepNewestFirst()
        {
            var released = new List<string>();
            var scope = new D3D11ConstructionScope();

            // The construction, with the fourth step throwing the way D3D11DxgiSwapchain.CreateWindows does.
            Action construct = () =>
            {
                scope.Track(new FakeStep("fences", released));
                scope.Track(new FakeStep("point sampler", released));
                scope.Track(new FakeStep("linear sampler", released));

                throw new InvalidOperationException("the swapchain step");
            };

            var thrown = Assert.Throws<InvalidOperationException>(construct);

            Assert.Empty(released);       // nothing is released until the unwind actually runs
            scope.Unwind();

            Assert.Equal(new[] { "linear sampler", "point sampler", "fences" }, released);
            Assert.Equal("the swapchain step", thrown.Message);
        }

        /// <summary>
        /// A COMMITTED CONSTRUCTION RELEASES NOTHING, which is the half that would turn the fix into a much worse
        /// bug than the leak: the device owns every one of those subsystems from the moment its constructor
        /// returns, and releasing them here would hand the caller a device whose fence timeline and swapchain
        /// were already destroyed.
        /// </summary>
        [Fact]
        public void ACommittedScope_ReleasesNothing()
        {
            var released = new List<string>();
            var scope = new D3D11ConstructionScope();

            scope.Track(new FakeStep("fences", released));
            scope.Track(new FakeStep("swapchain", released));
            scope.Commit();

            Assert.True(scope.IsCommitted);
            scope.Unwind();

            Assert.Empty(released);
        }

        /// <summary>
        /// A RELEASE THAT THROWS DOES NOT STOP THE ONES AFTER IT, AND DOES NOT REPLACE THE REAL FAILURE. The
        /// exception the caller has to see is the one that stopped construction, so a second exception raised
        /// while tidying up would bury it, and the releases the walk had not reached yet would never run.
        /// </summary>
        [Fact]
        public void AReleaseThatThrows_IsReportedAndTheWalkCarriesOn()
        {
            var released = new List<string>();
            var reported = new List<string>();
            var scope = new D3D11ConstructionScope(ex => reported.Add(ex.Message));

            scope.Track(new FakeStep("fences", released));
            scope.Track(new FakeStep("sampler", released, throwOnRelease: "the sampler release"));
            scope.Track(new FakeStep("swapchain", released));

            scope.Unwind();

            Assert.Equal(new[] { "swapchain", "fences" }, released);
            Assert.Equal(new[] { "the sampler release" }, reported);
        }

        /// <summary>
        /// A STEP WHOSE RELEASE IS NOT ITS OWN <c>Dispose</c> is registered the same way and unwinds in the same
        /// place. That is the device's shared sampler pair: it is deliberately NON-owning, so its
        /// <c>Dispose</c> is the no-op that protects it from a consumer, and only the device's own destroy frees
        /// the sampler state (https://github.com/APKiwiOrg/KhaozEngine/issues/506).
        /// </summary>
        [Fact]
        public void ANonOwningStepUnwindsThroughItsOwnRelease()
        {
            var released = new List<string>();
            var scope = new D3D11ConstructionScope();

            scope.Track(new FakeStep("fences", released));
            scope.TrackRelease(() => released.Add("shared sampler destroy"));

            Assert.Equal(2, scope.TrackedCount);
            scope.Unwind();

            Assert.Equal(new[] { "shared sampler destroy", "fences" }, released);
        }

        /// <summary>Unwinding twice releases once. A constructor that caught, unwound and rethrew into a caller
        /// that disposed the same scope would otherwise double-release every COM object it holds.</summary>
        [Fact]
        public void UnwindIsIdempotent()
        {
            var released = new List<string>();
            var scope = new D3D11ConstructionScope();

            scope.Track(new FakeStep("fences", released));

            scope.Unwind();
            scope.Unwind();

            Assert.Equal(new[] { "fences" }, released);
        }

        sealed class FakeStep : IDisposable
        {
            readonly string _name;
            readonly List<string> _released;
            readonly string? _throwOnRelease;

            internal FakeStep(string name, List<string> released, string? throwOnRelease = null)
            {
                _name = name;
                _released = released;
                _throwOnRelease = throwOnRelease;
            }

            public void Dispose()
            {
                if (_throwOnRelease != null) throw new InvalidOperationException(_throwOnRelease);
                _released.Add(_name);
            }
        }
    }
}
