using System;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STAGING MAP PATH'S DEVICE-FREE HALF: the two refusals a caller can earn, the row-pitch arithmetic a
    /// readback walks, decision G3's second check site, and decision W4's staging lock clause, all driven on
    /// macOS and Linux.
    /// <para>
    /// THE LOCK CLAUSE IS HERE NOW, WHICH IT WAS NOT. <see cref="D3D11StagingAccess"/> used to take a concrete
    /// <c>ID3D11DeviceContext</c>, so it could not be constructed off Windows and "the lock is taken for the map
    /// call and nothing longer" shipped as prose with no test behind it. Its four native calls now sit behind
    /// <see cref="ID3D11StagingMemory"/>, the way the ring's two sit behind <c>ID3D11RingMemory</c>, so the whole
    /// ordering is driven here through <see cref="FakeD3D11StagingMemory"/>, which records
    /// <c>Monitor.IsEntered</c> per call.
    /// </para>
    /// <para>
    /// WHAT IS STILL DELIBERATELY NOT HERE is the residue behind that seam:
    /// <see cref="D3D11ContextStagingMemory"/>'s four <c>Map</c> and <c>Unmap</c> calls against a live context,
    /// and which Vortice method each one picks. That needs a device and it is what the WARP leg covers.
    /// </para>
    /// </summary>
    public sealed class D3D11StagingMapTests
    {
        sealed class FakeRemovedReason : ID3D11RemovedReason
        {
            internal int Reason { get; set; } = D3D11DeviceLossCodes.DeviceHung;
            internal int Reads { get; private set; }

            public int GetDeviceRemovedReason()
            {
                Reads++;
                return Reason;
            }
        }

        // ---- The two refusals ---------------------------------------------------------------------------------

        /// <summary>
        /// A SECOND MAP OF THE SAME RESOURCE IS REFUSED BY NAME. Direct3D 11 answers it with a failed HRESULT and
        /// a debug-layer message, both of which are silent in a release build, so the shape it takes in the field
        /// is a readback that quietly returns the previous contents. The refusal names the mode the first mapping
        /// took, because the usual cause is a readback path that returned early without unmapping.
        /// </summary>
        [Fact]
        public void ASecondMapOfTheSameResource_IsRefusedByName()
        {
            var maps = new D3D11StagingMaps();
            var resource = new object();

            maps.Open(resource, GpuMapMode.Read);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => maps.Open(resource, GpuMapMode.Write));
            Assert.Contains("already mapped", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(GpuMapMode.Read), ex.Message, StringComparison.Ordinal);
            Assert.True(maps.IsMapped(resource));
            Assert.Equal(1, maps.OpenCount);
        }

        /// <summary>
        /// AN UNMAP OF SOMETHING THAT WAS NEVER MAPPED IS REFUSED TOO, and that one is the more valuable of the
        /// pair: Direct3D 11 ignores it entirely, so an unbalanced pair produces no signal at all and shows up
        /// later as a resource nobody can copy into.
        /// </summary>
        [Fact]
        public void AnUnmapWithNoMap_IsRefusedByName()
        {
            var maps = new D3D11StagingMaps();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => maps.Close(new object()));
            Assert.Contains("not mapped", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>The balanced pair is the ordinary case, and a resource may be mapped again once it has been
        /// released, which is what every frame of a readback loop does.</summary>
        [Fact]
        public void ABalancedPair_LeavesNothingOpenAndMayBeTakenAgain()
        {
            var maps = new D3D11StagingMaps();
            var resource = new object();

            maps.Open(resource, GpuMapMode.Read);
            maps.Close(resource);

            Assert.Equal(0, maps.OpenCount);
            Assert.False(maps.IsMapped(resource));

            maps.Open(resource, GpuMapMode.Read);
            Assert.Equal(1, maps.OpenCount);
        }

        /// <summary>
        /// TWO RESOURCES ARE TWO MAPPINGS, tracked by REFERENCE rather than by equality. Nothing in the seam gives
        /// a staging resource value equality, and a comparer that used it would let two equal-looking wrappers
        /// collide into one registry entry.
        /// </summary>
        [Fact]
        public void TwoResources_AreTrackedSeparately()
        {
            var maps = new D3D11StagingMaps();
            var first = new object();
            var second = new object();

            maps.Open(first, GpuMapMode.Read);
            maps.Open(second, GpuMapMode.Read);

            Assert.Equal(2, maps.OpenCount);
            maps.Close(first);
            Assert.True(maps.IsMapped(second));
        }

        /// <summary>
        /// TEARDOWN AND A DEVICE LOSS FORGET RATHER THAN UNMAP, and answer how many were open. After the device is
        /// gone the mappings do not exist, so re-issuing an <c>Unmap</c> against it is exactly the
        /// release-against-freed-memory that decision X3's liveness token exists to stop.
        /// </summary>
        [Fact]
        public void Forget_DropsEveryOpenMappingAndReportsHowMany()
        {
            var maps = new D3D11StagingMaps();
            maps.Open(new object(), GpuMapMode.Read);
            maps.Open(new object(), GpuMapMode.ReadWrite);

            Assert.Equal(2, maps.Forget());
            Assert.Equal(0, maps.OpenCount);
            Assert.Equal(0, maps.Forget());
        }

        // ---- The row pitch, which is the whole reason MappedData carries one -----------------------------------

        /// <summary>
        /// A TEXTURE'S MAPPED SIZE FOLLOWS THE RUNTIME'S PITCH, NOT THE TEXTURE'S OWN BYTE COUNT. Direct3D 11 pads
        /// each row of a mapped staging texture up to its own alignment, so a 300-pixel-wide RGBA texture commonly
        /// comes back at a 1280-byte pitch rather than 1200. A reader that walked it as packed rows would skew the
        /// image by five pixels per row, which is the failure <c>GpuReadback</c>'s unpack-by-pitch loop exists to
        /// prevent, and reporting a size that ignored the padding would make that loop read past the mapping on
        /// the last row.
        /// </summary>
        [Fact]
        public void ATextureMapping_ReportsThePaddedPitchAndTheSizeThatFollowsIt()
        {
            MappedData mapped = D3D11StagingMaps.ForTexture(new IntPtr(0x1000), rowPitchBytes: 1280, height: 300);

            Assert.Equal(new IntPtr(0x1000), mapped.Data);
            Assert.Equal(1280u, mapped.RowPitch);
            Assert.Equal(1280u * 300u, mapped.SizeInBytes);
        }

        /// <summary>A zero-height texture cannot exist, and the arithmetic still answers one row rather than a
        /// zero-byte mapping, so a caller that reads the base pointer gets a window rather than nothing.</summary>
        [Fact]
        public void ATextureMappingOfNoHeight_StillCoversOneRow()
        {
            MappedData mapped = D3D11StagingMaps.ForTexture(new IntPtr(0x20), rowPitchBytes: 64, height: 0);

            Assert.Equal(64u, mapped.SizeInBytes);
        }

        /// <summary>
        /// A BUFFER'S ROW PITCH IS ITS SIZE, which is what the seam documents in as many words. It is answered
        /// that way rather than as zero because <c>GpuReadback.ReadBuffer</c> and the Veldrid path both read the
        /// field, and a zero would turn a stride into a division by nothing.
        /// </summary>
        [Fact]
        public void ABufferMapping_ReportsItsSizeAsTheRowPitch()
        {
            MappedData mapped = D3D11StagingMaps.ForBuffer(new IntPtr(0x40), sizeInBytes: 4096);

            Assert.Equal(4096u, mapped.RowPitch);
            Assert.Equal(4096u, mapped.SizeInBytes);
        }

        /// <summary>Every staging map on this seam names subresource 0, because <c>Map(IGpuTexture, GpuMapMode)</c>
        /// carries no mip and no layer, exactly as <c>ResolveTexture</c> carries none. Pinned as a constant so the
        /// day the seam grows a mip parameter this fails rather than silently reading the base level.</summary>
        [Fact]
        public void EveryStagingMap_NamesSubresourceZero()
            => Assert.Equal(0, D3D11StagingMaps.Subresource);

        // ---- Decision G3's second check site --------------------------------------------------------------------

        /// <summary>A successful map costs no latch work and no reason read at all, which matters because this
        /// runs on every readback.</summary>
        [Theory]
        [InlineData(D3D11DeviceLossCodes.Ok)]
        [InlineData(1)]
        public void ASuccessfulMap_AsksTheLatchNothing(int hresult)
        {
            var reason = new FakeRemovedReason();
            var latch = new D3D11DeviceLossLatch(new D3D11DeviceLiveness(), reason);

            D3D11StagingMaps.RequireMapped(hresult, latch);

            Assert.False(latch.IsLost);
            Assert.Equal(0, reason.Reads);
        }

        /// <summary>
        /// A FAILED MAP THROWS RATHER THAN HANDING BACK THE NULL POINTER IT LEFT BEHIND. This is the one place the
        /// result of <c>ID3D11DeviceContext::Map</c> is interpreted, and it matters because Vortice returns it
        /// rather than throwing: a caller that ignored it would read through null and report an empty readback
        /// with nothing logged anywhere.
        /// </summary>
        [Fact]
        public void AnOrdinaryFailedMap_ThrowsWithoutLatchingADeviceLoss()
        {
            var reason = new FakeRemovedReason();
            var latch = new D3D11DeviceLossLatch(new D3D11DeviceLiveness(), reason);

            Assert.Throws<InvalidOperationException>(
                () => D3D11StagingMaps.RequireMapped(D3D11DeviceLossCodes.InvalidCall, latch));

            Assert.False(latch.IsLost);
            Assert.Equal(0, reason.Reads);
        }

        /// <summary>
        /// DECISION G3 AT THIS SITE: a map that fails with a REMOVAL latches immediately, under this row's own
        /// site name, and calls <c>GetDeviceRemovedReason</c> exactly once. The latch's own remarks already name
        /// the staging map as its second site and say the call site belongs to this row, so this is the assertion
        /// that the call site actually arrived.
        /// </summary>
        [Fact]
        public void AMapThatFailsWithARemoval_LatchesImmediatelyUnderThisSite()
        {
            var reason = new FakeRemovedReason { Reason = D3D11DeviceLossCodes.DeviceHung };
            var liveness = new D3D11DeviceLiveness();
            var latch = new D3D11DeviceLossLatch(liveness, reason);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => D3D11StagingMaps.RequireMapped(D3D11DeviceLossCodes.DeviceRemoved, latch));

            Assert.True(latch.IsLost);
            Assert.True(liveness.IsDead);
            Assert.Equal(D3D11StagingMaps.MapSite, latch.Site);
            Assert.Equal(D3D11DeviceLossCodes.DeviceHung, latch.RemovedReason);
            Assert.Equal(1, reason.Reads);
            Assert.Contains("LOST", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE LATCH IS OPTIONAL, AND A NULL ONE STILL THROWS. That is the state until the device row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/497) wires one, and it is the honest degradation: a
        /// failed map is still a failed map, and the only thing missing is the attribution.
        /// </summary>
        [Fact]
        public void WithNoLatchWired_AFailedMapStillThrows()
            => Assert.Throws<InvalidOperationException>(
                () => D3D11StagingMaps.RequireMapped(D3D11DeviceLossCodes.DeviceRemoved, loss: null));

        // ---- Decision W4's staging lock clause, through the seam ------------------------------------------------

        /// <summary>
        /// EVERY NATIVE CALL RUNS UNDER THE SUBMIT LOCK, which is the positive half of the staging clause. A map
        /// and an unmap are both context calls, so both owe it, and a buffer and a texture take four different
        /// members that each have to remember. The fake answers null rather than false when nothing recorded, so
        /// a wiring mistake in the test cannot read as a pass.
        /// </summary>
        [Fact]
        public void EveryNativeStagingCall_RunsUnderTheSubmitLock()
        {
            var submitLock = new object();
            using var memory = new FakeD3D11StagingMemory { SubmitLock = submitLock };
            var access = new D3D11StagingAccess(memory, submitLock);
            using var buffer = new FakeStagingBuffer();
            using var texture = new FakeStagingTexture();

            access.Map(buffer, GpuMapMode.Read);
            access.Unmap(buffer);
            access.Map(texture, GpuMapMode.Read);
            access.Unmap(texture);

            Assert.Equal(
                new[] { "MapBuffer", "UnmapBuffer", "MapTexture", "UnmapTexture" },
                memory.Calls);
            Assert.True(memory.EveryCallHeldTheSubmitLock);
        }

        /// <summary>
        /// AND THE LOCK IS NOT HELD ACROSS THE CALLER'S READ, which is the half that matters and the half a
        /// contract stated only in prose loses first. A readback is Map, walk the pixels, Unmap, and holding the
        /// lock through the walk would block every submit for as long as the consumer took: that is precisely the
        /// frame-long hold this design deletes. Two calls take the lock twice, and between them the mapped pointer
        /// is the caller's alone.
        /// </summary>
        [Fact]
        public void BetweenMapAndUnmap_TheCallerReadsWithTheSubmitLockFree()
        {
            var submitLock = new object();
            using var memory = new FakeD3D11StagingMemory { SubmitLock = submitLock };
            var access = new D3D11StagingAccess(memory, submitLock);
            using var buffer = new FakeStagingBuffer(sizeInBytes: 256);

            MappedData mapped = access.Map(buffer, GpuMapMode.Read);
            Assert.True(memory.LastCallHeldTheSubmitLock);

            // The consumer's walk. This is the window a frame-long hold would have covered.
            bool lockHeldDuringTheRead = Monitor.IsEntered(submitLock);
            memory.Bytes[0] = 0xAB;
            byte firstByte = Marshal.ReadByte(mapped.Data);

            access.Unmap(buffer);

            Assert.False(lockHeldDuringTheRead);
            Assert.Equal(0xAB, firstByte);
            Assert.True(memory.LastCallHeldTheSubmitLock);
            Assert.Equal(256u, mapped.RowPitch);
        }

        /// <summary>
        /// BOTH REFUSALS STILL FIRE THROUGH THE REAL PATH, not only against the registry in isolation. The
        /// refusal is taken INSIDE the lock and BEFORE the native call, so a refused pair costs no driver call at
        /// all, and that ordering is what these assert: the fake was never asked for a second map, and never asked
        /// to unmap something it holds nothing for.
        /// </summary>
        [Fact]
        public void ThroughTheSeam_ADoubleMapAndAnUnmapWithNoMap_AreRefusedBeforeAnyNativeCall()
        {
            var submitLock = new object();
            using var memory = new FakeD3D11StagingMemory { SubmitLock = submitLock };
            var access = new D3D11StagingAccess(memory, submitLock);
            using var buffer = new FakeStagingBuffer();
            using var never = new FakeStagingBuffer();

            access.Map(buffer, GpuMapMode.Read);

            InvalidOperationException second = Assert.Throws<InvalidOperationException>(
                () => access.Map(buffer, GpuMapMode.Write));
            Assert.Contains("already mapped", second.Message, StringComparison.Ordinal);

            InvalidOperationException unbalanced = Assert.Throws<InvalidOperationException>(
                () => access.Unmap(never));
            Assert.Contains("not mapped", unbalanced.Message, StringComparison.Ordinal);

            // One map, and nothing else reached the driver. The first mapping is still open and still balanced.
            Assert.Equal(new[] { "MapBuffer" }, memory.Calls);
            Assert.Equal(1, access.Maps.OpenCount);
            access.Unmap(buffer);
            Assert.Equal(0, access.Maps.OpenCount);
        }

        /// <summary>
        /// DECISION G3'S SITE DRIVEN THROUGH THE PATH A DEVICE TAKES, with the fake answering a removal HRESULT.
        /// The static's own arms are asserted above. What this adds is that the result actually travels from the
        /// native call to <c>RequireMapped</c> with the latch attached, and that a failed map ROLLS THE REGISTRY
        /// BACK. Leaving the record would make the caller's next attempt look like a double map and refuse it for
        /// the wrong reason, which is a failure that outlives the one that caused it.
        /// </summary>
        [Fact]
        public void AMapWhoseHresultIsARemoval_LatchesAndLeavesNothingOpen()
        {
            var submitLock = new object();
            var reason = new FakeRemovedReason { Reason = D3D11DeviceLossCodes.DeviceHung };
            var liveness = new D3D11DeviceLiveness();
            var latch = new D3D11DeviceLossLatch(liveness, reason);
            using var memory = new FakeD3D11StagingMemory
            {
                SubmitLock = submitLock,
                MapResult = D3D11DeviceLossCodes.DeviceRemoved,
            };
            var access = new D3D11StagingAccess(memory, submitLock, latch);
            using var texture = new FakeStagingTexture();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => access.Map(texture, GpuMapMode.Read));

            Assert.Contains("LOST", ex.Message, StringComparison.Ordinal);
            Assert.True(latch.IsLost);
            Assert.True(liveness.IsDead);
            Assert.Equal(D3D11StagingMaps.MapSite, latch.Site);
            Assert.Equal(D3D11DeviceLossCodes.DeviceHung, latch.RemovedReason);

            // The rollback: nothing is recorded as open, so the next attempt is a first map rather than a second.
            Assert.Equal(0, access.Maps.OpenCount);
            Assert.True(memory.EveryCallHeldTheSubmitLock);
        }

        /// <summary>
        /// A TEXTURE'S MAPPED WINDOW COMES FROM THE RUNTIME'S PITCH AND THE TEXTURE'S HEIGHT, joined here rather
        /// than in the arithmetic helper alone. The fake reports a pitch unlike any packed width, so a size
        /// computed from the texture's own byte count would fail this rather than coincide with it.
        /// </summary>
        [Fact]
        public void ATextureMapThroughTheSeam_ReportsTheRuntimePitchAndTheHeightItCovers()
        {
            var submitLock = new object();
            using var memory = new FakeD3D11StagingMemory { SubmitLock = submitLock, RowPitch = 96 };
            var access = new D3D11StagingAccess(memory, submitLock);
            using var texture = new FakeStagingTexture(width: 4, height: 3);

            MappedData mapped = access.Map(texture, GpuMapMode.Read);

            Assert.Equal(96u, mapped.RowPitch);
            Assert.Equal(96u * 3u, mapped.SizeInBytes);
            access.Unmap(texture);
        }

        /// <summary>
        /// A RESOURCE WITH NO CPU ACCESS IS REFUSED BEFORE THE LOCK IS EVEN TAKEN, and a resource from another
        /// backend is refused by name. Both used to be a cast to a Windows-only concrete type and therefore
        /// unreachable from here, and both are answered by the capability seam now, so both are pinned. Neither
        /// reaches the driver.
        /// </summary>
        [Fact]
        public void AnUnmappableResourceAndAForeignOne_AreRefusedWithoutANativeCall()
        {
            var submitLock = new object();
            using var memory = new FakeD3D11StagingMemory { SubmitLock = submitLock };
            var access = new D3D11StagingAccess(memory, submitLock);
            using var defaultUsage = new FakeStagingBuffer(mappable: false);
            using var foreign = new FakeBuffer(256);

            ArgumentException notStaging = Assert.Throws<ArgumentException>(
                () => access.Map(defaultUsage, GpuMapMode.Read));
            Assert.Contains("GpuBufferUsage.Staging", notStaging.Message, StringComparison.Ordinal);

            ArgumentException otherBackend = Assert.Throws<ArgumentException>(
                () => access.Map(foreign, GpuMapMode.Read));
            Assert.Contains("another backend", otherBackend.Message, StringComparison.Ordinal);

            Assert.Empty(memory.Calls);
            Assert.Equal(0, access.Maps.OpenCount);
        }

        // ---- The load-path claim ------------------------------------------------------------------------------

        /// <summary>
        /// THE WHOLE STAGING SURFACE THAT IS NOT A NATIVE CALL RUNS OFF WINDOWS WITHOUT LOADING THE INTEROP, which
        /// is decision P1's claim applied to this row. It covers MORE than it used to: the seam extraction moved
        /// the Vortice reference out of <see cref="D3D11StagingAccess"/> and into
        /// <see cref="D3D11ContextStagingMemory"/>, so the whole map path (the lock, the registry, the
        /// arithmetic and the G3 site) is exercised here through a fake seam and the interop still must not
        /// appear. The members that DO name a Direct3D type live one class further down and are unreachable from
        /// here by construction, since building one needs an <c>ID3D11DeviceContext</c>, and its
        /// <c>ToMapMode</c> is marked Windows-only so calling it from a test project that is not would be a build
        /// error rather than a load.
        /// </summary>
        [Fact]
        public void OffWindows_TheStagingBookkeepingRunsWithoutLoadingTheDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            var maps = new D3D11StagingMaps();
            var resource = new object();
            maps.Open(resource, GpuMapMode.Read);
            _ = maps.IsMapped(resource);
            _ = maps.OpenCount;
            maps.Close(resource);
            maps.Open(resource, GpuMapMode.ReadWrite);
            _ = maps.Forget();
            _ = D3D11StagingMaps.ForTexture(IntPtr.Zero, 256, 4);
            _ = D3D11StagingMaps.ForBuffer(IntPtr.Zero, 256);
            D3D11StagingMaps.RequireMapped(D3D11DeviceLossCodes.Ok, loss: null);

            var submitLock = new object();
            using var memory = new FakeD3D11StagingMemory { SubmitLock = submitLock };
            var access = new D3D11StagingAccess(memory, submitLock);
            using var buffer = new FakeStagingBuffer();
            using var texture = new FakeStagingTexture();
            _ = access.Map(buffer, GpuMapMode.Read);
            access.Unmap(buffer);
            _ = access.Map(texture, GpuMapMode.ReadWrite);
            access.Unmap(texture);

            D3D11InteropLoad.AssertNotLoaded();
        }
    }
}
