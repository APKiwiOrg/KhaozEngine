using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE TWO NATIVE CALLS A CONSTANT-BUFFER RING IS MADE OF, behind an interface for the same reason
    /// <see cref="ID3D11FenceTimeline"/> is one: everything that can be WRONG about a ring (which segment a write
    /// lands in, when the map happens, when the unmap happens, whether a segment is safe to reuse) is engine
    /// logic, and it is tested by a plain <c>[Fact]</c> on macOS and Linux with a fake behind this interface. What
    /// is left on the far side is a <c>Map</c> and an <c>Unmap</c>.
    /// <para>
    /// ONE INSTANCE PER RING-BACKED BUFFER, holding that buffer and the immediate context. A ring is one
    /// <c>ID3D11Buffer</c> of <c>segmentStride * framesInFlight</c> bytes and subresource 0 is the whole of it, so
    /// there is no subresource parameter here and never will be.
    /// </para>
    /// <para>
    /// IT DOES NOT OWN THE BUFFER. <see cref="D3D11Buffer"/> creates the native buffer and releases it, and this
    /// only maps it, which is why nothing here is <see cref="IDisposable"/>. The ring unmaps before the buffer is
    /// released, because releasing a mapped resource leaves the runtime holding a pointer into memory nobody owns.
    /// </para>
    /// </summary>
    internal interface ID3D11RingMemory
    {
        /// <summary>
        /// Map the whole ring for writing with <c>MAP_WRITE_NO_OVERWRITE</c> and return the pointer to its first
        /// byte. Valid until <see cref="Unmap"/>.
        /// <para>
        /// NO_OVERWRITE IS A PROMISE, and the segment rotation is what keeps it. It tells the driver the CPU will
        /// not touch bytes the GPU is still reading, so the driver hands back the same allocation rather than
        /// orphaning it the way <c>WRITE_DISCARD</c> does, and no work in flight is disturbed. The ring keeps that
        /// promise structurally: a frame writes only its own segment, and a segment is not handed out until the
        /// completion timeline says the submission that last used it has finished. Break that and this call
        /// becomes a silent corruption rather than an error, which is why the gate is a fence read and not a frame
        /// count.
        /// </para>
        /// <para>
        /// It needs the <c>MapNoOverwriteOnDynamicConstantBuffer</c> device feature, which the backend's machine
        /// probe makes a hard requirement, so a machine that cannot do this never reaches a ring at all.
        /// </para>
        /// </summary>
        IntPtr MapWriteNoOverwrite();

        /// <summary>Release the mapping so the GPU may read the buffer again. Called at the start of the next
        /// submit under the deferred driver, and after each write under the immediate one (see
        /// <see cref="D3D11RingMapScope"/>).</summary>
        void Unmap();
    }
}
