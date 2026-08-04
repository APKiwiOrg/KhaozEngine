using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE FOUR NATIVE CALLS A STAGING MAP IS MADE OF, behind an interface for the same reason
    /// <see cref="ID3D11RingMemory"/> and <see cref="ID3D11FenceTimeline"/> are: everything that can be WRONG
    /// about a staging map (which subresource it names, whether the pair is balanced, what the row pitch means,
    /// what a failed HRESULT does, and WHEN THE SUBMIT LOCK IS HELD) is engine logic, and it is tested by a plain
    /// <c>[Fact]</c> on macOS and Linux with a fake behind this interface. What is left on the far side is
    /// <c>ID3D11DeviceContext.Map</c> and <c>Unmap</c>.
    /// <para>
    /// THE LOCK CONTRACT IS THE REASON THIS SEAM EXISTS AT ALL, rather than a tidiness argument. Decision W4 says
    /// staging <c>Map</c> and <c>Unmap</c> take the submit lock for the duration of that one call and nothing
    /// longer, and the half that matters is the NEGATIVE half: the lock is NOT held across the caller's read. With
    /// a concrete <c>ID3D11DeviceContext</c> on the far side that clause could only be verified on Windows, so it
    /// shipped as prose. A fake here records <c>Monitor.IsEntered</c> per call, exactly as
    /// <c>FakeD3D11Ring</c>'s <c>LastMapHeldTheSubmitLock</c> and <c>FakeD3D11FenceTimeline</c> do for their own
    /// seams, and the clause becomes an assertion.
    /// </para>
    /// <para>
    /// THE RESOURCE ARRIVES AS <c>object</c>, which is the same choice <see cref="ID3D11BindableViews"/> makes and
    /// for the same reason. <see cref="D3D11Buffer"/> and <see cref="D3D11Texture"/> are
    /// <c>[SupportedOSPlatform("windows")]</c> at the type level and their native handles are typed Direct3D
    /// handles, so a seam naming them could not be compiled into a body that runs everywhere. The implementation
    /// casts once, at the Windows boundary.
    /// </para>
    /// <para>
    /// A MAP ANSWERS ITS HRESULT RATHER THAN INTERPRETING IT. <c>ID3D11DeviceContext::Map</c> hands failure back as
    /// a result rather than as a throw, and what that result MEANS (including asking the device-loss latch, which
    /// is decision G3's second check site) is <see cref="D3D11StagingMaps.RequireMapped"/>, device-free and
    /// therefore testable. So the result travels across this seam untouched and a fake can hand back any HRESULT
    /// it likes.
    /// </para>
    /// <para>
    /// ONE INSTANCE PER DEVICE, holding the immediate context. It owns neither the context nor any resource
    /// handed to it, which is why nothing here is <see cref="IDisposable"/>. Taking the submit lock is the
    /// CALLER's job (<see cref="D3D11StagingAccess"/>), so nothing behind this seam thinks about locking.
    /// </para>
    /// </summary>
    internal interface ID3D11StagingMemory
    {
        /// <summary>
        /// Map a staging BUFFER's whole range, answering the <c>HRESULT</c> and the pointer the runtime handed
        /// back. Direct3D 11 maps a buffer as one flat range with no subresource to choose and no pitch to
        /// report, which is why this is a different member from <see cref="MapTexture"/> rather than one with an
        /// ignored output.
        /// </summary>
        /// <param name="buffer">The native buffer, as <see cref="ID3D11BindableViews"/> hands one over.</param>
        /// <param name="mode">The seam mode, translated to a Direct3D map mode at the Windows boundary.</param>
        /// <param name="data">The mapped pointer, or <see cref="IntPtr.Zero"/> when the map failed.</param>
        int MapBuffer(object buffer, GpuMapMode mode, out IntPtr data);

        /// <summary>Release a staging buffer's mapping.</summary>
        void UnmapBuffer(object buffer);

        /// <summary>
        /// Map a staging TEXTURE at subresource 0, answering the <c>HRESULT</c>, the pointer, and the row pitch
        /// the runtime CHOSE. The pitch is commonly larger than the packed row width, which is the whole reason
        /// <see cref="MappedData.RowPitch"/> is on the seam, so it is reported rather than derived.
        /// </summary>
        /// <param name="texture">The native texture, object-typed for the reason the type remarks give.</param>
        /// <param name="mode">The seam mode.</param>
        /// <param name="data">The mapped pointer, or <see cref="IntPtr.Zero"/> when the map failed.</param>
        /// <param name="rowPitchBytes">The runtime's chosen pitch, or zero when the map failed.</param>
        int MapTexture(object texture, GpuMapMode mode, out IntPtr data, out uint rowPitchBytes);

        /// <summary>Release a staging texture's mapping.</summary>
        void UnmapTexture(object texture);
    }

    /// <summary>
    /// WHAT A STAGING MAP NEEDS FROM THE RESOURCE IT WAS HANDED, and the seam that keeps both of the map path's
    /// refusals device-free. The fourth internal capability seam in this package, after
    /// <see cref="ID3D11PipelineState"/>, <see cref="ID3D11RingBacked"/> and <see cref="ID3D11BindableViews"/>,
    /// and it exists for the reasons that one gives: every member is <c>object</c> or a primitive, so
    /// <see cref="D3D11StagingAccess"/> can decide WHETHER a resource may be mapped and WHICH native handle to
    /// name without itself naming a Direct3D type.
    /// <para>
    /// WITHOUT IT THE TWO REFUSALS WOULD MOVE TO THE WINDOWS SIDE, which is the wrong direction for this package.
    /// A cast straight to <see cref="D3D11Buffer"/> is a cast to a Windows-only type, so a device-free test could
    /// never reach past it, and the "was this created staging?" check would have to travel with it. Asking the
    /// resource instead means a foreign backend's buffer is refused by name here, on every platform, and the
    /// Windows residue stays the four native calls and nothing else.
    /// </para>
    /// </summary>
    internal interface ID3D11MappableResource
    {
        /// <summary>The native resource a <c>Map</c> names (<c>ID3D11Buffer</c> or <c>ID3D11Texture2D</c>),
        /// object-typed. Never null: a resource this backend created always has one.</summary>
        object MapTarget { get; }

        /// <summary>
        /// Whether the declared usage gave this resource CPU access at all. False for a DEFAULT-usage resource,
        /// which Direct3D 11 refuses to map by handing back a null pointer that reads as an empty readback rather
        /// than as a failure.
        /// </summary>
        bool IsMappable { get; }
    }
}
