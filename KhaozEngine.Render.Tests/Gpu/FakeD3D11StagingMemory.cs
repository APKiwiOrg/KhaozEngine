using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A STAGING MAP'S NATIVE CALLS WITH NO DEVICE BEHIND THEM, so the ordering and the LOCKING above them are
    /// driven by plain <c>[Fact]</c>s on macOS and Linux as well as Windows. This is why
    /// <see cref="ID3D11StagingMemory"/> is an interface at all: what is left behind it on the real path is
    /// <c>Map</c> and <c>Unmap</c>, and the decision W4 clause that says WHEN the submit lock is held sits above
    /// it, in <see cref="D3D11StagingAccess"/>.
    /// <para>
    /// IT RECORDS <see cref="Monitor.IsEntered"/> PER CALL, which is the whole point and the same shape
    /// <c>FakeD3D11Ring</c>'s <c>LastMapHeldTheSubmitLock</c> and <c>FakeD3D11FenceTimeline</c>'s signal
    /// recording take. The positive half of the contract (every native call under the lock) and the negative half
    /// (the caller's read between <c>Map</c> and <c>Unmap</c> NOT under it) are both questions about who holds a
    /// monitor at a given instant, and neither can be asked of a concrete <c>ID3D11DeviceContext</c> off Windows.
    /// </para>
    /// <para>
    /// THE POINTER IS A PINNED MANAGED ARRAY, for the reason the ring's fake pins one: a test that wants to prove
    /// a readback walked real bytes needs the mapping to stay where it was handed out.
    /// </para>
    /// <para>
    /// <see cref="MapResult"/> IS THE HRESULT DIAL, and it is what makes decision G3's site reachable from here.
    /// The real <c>Map</c> answers a result rather than throwing, so a fake that could only succeed would leave
    /// the failure arm of <c>RequireMapped</c> testable only through the static, never through the path a device
    /// actually takes.
    /// </para>
    /// </summary>
    internal sealed class FakeD3D11StagingMemory : ID3D11StagingMemory, IDisposable
    {
        readonly byte[] _bytes;
        readonly List<string> _calls = new();
        readonly HashSet<object> _mapped = new(ReferenceEqualityComparer.Instance);
        GCHandle _pin;

        internal FakeD3D11StagingMemory(int bytes = 256)
        {
            _bytes = new byte[bytes];
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
        }

        /// <summary>The backing bytes, so a test can prove the caller read through the mapping it was handed.
        /// </summary>
        internal byte[] Bytes => _bytes;

        /// <summary>The native calls this fake was asked for, in order, by member name.</summary>
        internal IReadOnlyList<string> Calls => _calls;

        /// <summary>The <c>HRESULT</c> the next map answers. <see cref="D3D11DeviceLossCodes.Ok"/> by default, and
        /// the dial a test turns to reach decision G3's failure arm through the real map path.</summary>
        internal int MapResult { get; set; } = D3D11DeviceLossCodes.Ok;

        /// <summary>The row pitch a texture map reports. Deliberately unlike any packed width, so a test asserting
        /// the mapped size cannot pass on an arithmetic that ignored the pitch.</summary>
        internal uint RowPitch { get; set; } = 64;

        /// <summary>The submit lock the access under test was built with, so this fake can record whether each
        /// call arrived holding it. Left null by a test that does not care.</summary>
        internal object? SubmitLock { get; set; }

        /// <summary>Whether EVERY call so far ran with <see cref="SubmitLock"/> held. Null until something is
        /// called, and always null while <see cref="SubmitLock"/> is unset, so a test cannot read a false pass out
        /// of a fake it forgot to wire up. Every member here is a context call, so every one owes the lock.
        /// </summary>
        internal bool? EveryCallHeldTheSubmitLock { get; private set; }

        /// <summary>Whether the LAST call ran with the lock held, for a test that wants one call rather than the
        /// running answer.</summary>
        internal bool? LastCallHeldTheSubmitLock { get; private set; }

        /// <summary>Whether anything is mapped right now, by this fake's own reckoning rather than the registry's.
        /// </summary>
        internal bool IsMapped(object resource) => _mapped.Contains(resource);

        /// <inheritdoc/>
        public int MapBuffer(object buffer, GpuMapMode mode, out IntPtr data)
            => MapAny(nameof(MapBuffer), buffer, out data, out _);

        /// <inheritdoc/>
        public void UnmapBuffer(object buffer) => UnmapAny(nameof(UnmapBuffer), buffer);

        /// <inheritdoc/>
        public int MapTexture(object texture, GpuMapMode mode, out IntPtr data, out uint rowPitchBytes)
            => MapAny(nameof(MapTexture), texture, out data, out rowPitchBytes);

        /// <inheritdoc/>
        public void UnmapTexture(object texture) => UnmapAny(nameof(UnmapTexture), texture);

        public void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }

        int MapAny(string call, object resource, out IntPtr data, out uint rowPitchBytes)
        {
            Record(call);

            if (D3D11DeviceLossCodes.IsFailure(MapResult))
            {
                // Exactly what the shipped Windows body does with a failed map: no pointer, no pitch, and the
                // result handed back for the device-free half to interpret.
                data = IntPtr.Zero;
                rowPitchBytes = 0u;
                return MapResult;
            }

            if (!_mapped.Add(resource))
                throw new InvalidOperationException(
                    "A fake staging memory was asked to map a resource it already holds a mapping for. In "
                    + "production Direct3D 11 answers that with a failed HRESULT and a null pointer, which is why "
                    + "D3D11StagingMaps refuses it before the call is ever made.");

            data = _pin.AddrOfPinnedObject();
            rowPitchBytes = RowPitch;
            return MapResult;
        }

        void UnmapAny(string call, object resource)
        {
            Record(call);

            if (!_mapped.Remove(resource))
                throw new InvalidOperationException(
                    "A fake staging memory was asked to unmap a resource it holds no mapping for. In production "
                    + "Direct3D 11 ignores that entirely, which is why D3D11StagingMaps refuses it before the "
                    + "call is ever made.");
        }

        void Record(string call)
        {
            _calls.Add(call);

            if (SubmitLock is not object submitLock) return;

            bool held = Monitor.IsEntered(submitLock);
            LastCallHeldTheSubmitLock = held;
            EveryCallHeldTheSubmitLock = (EveryCallHeldTheSubmitLock ?? true) && held;
        }
    }

    /// <summary>
    /// A STAGING BUFFER WITH NO DEVICE BEHIND IT: the seam's <see cref="IGpuBuffer"/> plus the capability seam a
    /// staging map asks (<see cref="ID3D11MappableResource"/>), which is what lets the map path's two refusals be
    /// reached off Windows. The map target is a bare <see cref="object"/> standing in for the
    /// <c>ID3D11Buffer</c>, exactly as the real one hands its native handle over.
    /// </summary>
    internal sealed class FakeStagingBuffer : IGpuBuffer, ID3D11MappableResource
    {
        internal FakeStagingBuffer(uint sizeInBytes = 256, bool mappable = true)
        {
            SizeInBytes = sizeInBytes;
            IsMappable = mappable;
        }

        public uint SizeInBytes { get; }

        /// <inheritdoc/>
        public object MapTarget { get; } = new();

        /// <inheritdoc/>
        public bool IsMappable { get; }

        public void Dispose()
        {
        }
    }

    /// <summary>The texture half of <see cref="FakeStagingBuffer"/>, carrying the height a mapped size is
    /// computed from.</summary>
    internal sealed class FakeStagingTexture : IGpuTexture, ID3D11MappableResource
    {
        internal FakeStagingTexture(uint width = 4, uint height = 4, bool mappable = true)
        {
            Width = width;
            Height = height;
            IsMappable = mappable;
        }

        public uint Width { get; }

        public uint Height { get; }

        public uint MipLevels => 1;

        public uint SampleCount => 1;

        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8UNorm;

        /// <inheritdoc/>
        public object MapTarget { get; } = new();

        /// <inheritdoc/>
        public bool IsMappable { get; }

        public void Dispose()
        {
        }
    }
}
