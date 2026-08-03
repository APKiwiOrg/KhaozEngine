using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STAGING MAP PATH'S DEVICE-FREE HALF: the two refusals a caller can earn, the row-pitch arithmetic a
    /// readback walks, and decision G3's second check site, all driven on macOS and Linux.
    /// <para>
    /// WHAT IS DELIBERATELY NOT HERE. <see cref="D3D11StagingAccess"/> is the other half, and every member of it
    /// takes an <c>ID3D11DeviceContext</c>, so it cannot be constructed off Windows and its two native calls are
    /// the residue the WARP leg covers. Everything that could be wrong WITHOUT a GPU was pushed into
    /// <see cref="D3D11StagingMaps"/> precisely so it could be pinned here: which subresource a map names, what
    /// <see cref="MappedData.RowPitch"/> means for a texture versus a buffer, whether an unbalanced pair is
    /// refused, and what a failed HRESULT does to the device-loss latch.
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

        // ---- The load-path claim ------------------------------------------------------------------------------

        /// <summary>
        /// THE WHOLE STAGING SURFACE THAT IS NOT A NATIVE CALL RUNS OFF WINDOWS WITHOUT LOADING THE INTEROP, which
        /// is decision P1's claim applied to this row. The two members that DO name a Direct3D type
        /// (<c>D3D11StagingAccess.Map</c> and <c>Unmap</c>) are unreachable from here by construction, because
        /// constructing one needs an <c>ID3D11DeviceContext</c>, and <c>ToMapMode</c> is marked Windows-only so
        /// calling it from a test project that is not would be a build error rather than a load.
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

            D3D11InteropLoad.AssertNotLoaded();
        }
    }
}
