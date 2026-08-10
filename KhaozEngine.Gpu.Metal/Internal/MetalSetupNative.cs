using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE NATIVE HALF OF THE DEVICE-OWNED SETUP BATCH (M-M9): the seven Objective-C operations a batch makes,
    /// and nothing that decides anything.
    /// <para>
    /// THIS IS <see cref="IMetalSharedEvent"/>'s SPLIT APPLIED TO THE SETUP PATH, for the same reason. What is
    /// left behind this interface is message sends with no ordering logic in them, and everything that could be
    /// wrong about the BOOKKEEPING (which uploads share a batch, when a batch is committed, what the byte budget
    /// does, what a dead device releases and what it must not) sits above it in
    /// <see cref="MetalSetupCommands"/> where a plain <c>[Fact]</c> can reach it on a machine with no Metal at
    /// all. Before the split, none of it was reachable without a device: a nil <c>MTLCommandQueue</c> answers a
    /// nil command buffer, so every upload returned early and the batching was untestable off hardware.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that the driver accepts the eleven-argument copy, or that the buffer
    /// carrying it completes without an error. <c>MetalResourceGpuTests</c> is what proves that, under a
    /// <c>[GpuFact]</c> against a real device, and the boundary between the two is deliberate rather than an
    /// omission.
    /// </para>
    /// </summary>
    internal interface IMetalSetupNative
    {
        /// <summary>Open a batch: a fresh, RETAINED <c>MTLCommandBuffer</c> with the completion handler attached,
        /// or a nil handle when the queue would not make one. The caller owns the +1 until it passes the handle
        /// back to <see cref="ReleaseBatch"/>.</summary>
        MTLCommandBuffer BeginBatch();

        /// <summary>Allocate a Shared <c>MTLBuffer</c> holding a copy of <paramref name="data"/>. Throws by name
        /// rather than answering nil, because a caller with nowhere to put the bytes has nothing to fall back
        /// to.</summary>
        MTLBuffer Stage(ReadOnlySpan<byte> data);

        /// <summary>Record one buffer-to-texture copy into <paramref name="batch"/>, opening and ending a blit
        /// encoder inside the call.</summary>
        void Encode(MTLCommandBuffer batch, MTLBuffer staged, MTLTexture destination, ulong sourceRowPitch,
            in MetalTextureUpload upload);

        /// <summary>Commit the batch, which enqueues it. It does not wait: the wait belongs to whichever drain
        /// the caller was going to do anyway.</summary>
        void Commit(MTLCommandBuffer batch);

        /// <summary>Release the +1 <see cref="BeginBatch"/> took. The driver keeps its own reference to a
        /// committed buffer until it completes, so this releases the holder's claim rather than the
        /// buffer.</summary>
        void ReleaseBatch(MTLCommandBuffer batch);

        /// <summary>Release one staging buffer. <c>-newBufferWithLength:options:</c> follows the new rule, so
        /// this is the owner's single release.</summary>
        void ReleaseStaging(MTLBuffer staged);

        /// <summary>Read a batch's <c>-status</c> and <c>-error</c>, which is M-G4's reading taken off the setup
        /// path rather than off a frame.</summary>
        MetalCommandBufferFault ReadFault(MTLCommandBuffer batch);
    }

    /// <summary>
    /// The real one: the device and the queue a setup batch is built on, and seven bodies that are message sends
    /// and nothing else.
    /// <para>
    /// EVERY BODY OPENS AN AUTORELEASE POOL (M-N5), and here that is load-bearing rather than uniform. A batch
    /// reaches <c>-commandBuffer</c> and <c>-blitCommandEncoder</c>, both AUTORELEASED, on whatever thread a
    /// consumer loads content on, which is exactly the thread whose implicit pool drains next never. The pools
    /// used to sit in <see cref="MetalSetupCommands"/>, and they moved here with the calls they cover.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MetalSetupNative : IMetalSetupNative
    {
        readonly MTLDevice _device;
        readonly MTLCommandQueue _queue;

        /// <param name="device">The device staging buffers are allocated on.</param>
        /// <param name="queue">The device's one queue (M-N2), which every setup batch is committed to.</param>
        internal MetalSetupNative(MTLDevice device, MTLCommandQueue queue)
        {
            _device = device;
            _queue = queue;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE RETAIN IS THE POINT OF THIS MEMBER. The buffer outlives the pool the queue handed it out in,
        /// because the whole of M-M9 is that uploads from separate calls share one batch, and the pop at the end
        /// of this call would otherwise free it under the next append. Released once at the commit's successor or
        /// at teardown.
        /// <para>
        /// The completion handler's only job is M-G4's error latch (M-F2), and a setup batch can fail exactly as
        /// a frame can. It is inert until the device registers its queue with the handler, which is the
        /// command-list row's wiring (https://github.com/APKiwiOrg/KhaozEngine/issues/573).
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public MTLCommandBuffer BeginBatch()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // A fresh command buffer every time, because an MTLCommandBuffer is single-use and there is no reset,
            // no pool object and no allocator to choose between (M-R2).
            MTLCommandBuffer batch = _queue.CommandBuffer();
            if (batch.IsNull) return batch;

            batch.Retain();
            _ = MetalCompletionHandler.AttachTo(batch.Handle);
            return batch;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public MTLBuffer Stage(ReadOnlySpan<byte> data)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLBuffer staged = _device.NewBuffer((nuint)data.Length, MTLResourceOptions.SharedDefaultCache);
            if (staged.IsNull)
            {
                throw new InvalidOperationException(
                    "The native Metal device would not allocate a " + data.Length
                    + "-byte Shared staging buffer for a device-level texture upload.");
            }

            IntPtr contents = staged.Contents();
            if (contents == IntPtr.Zero)
            {
                staged.Release();
                throw new InvalidOperationException(
                    "A Shared MTLBuffer staging a texture upload answered a null -contents pointer.");
            }

            unsafe { data.CopyTo(new Span<byte>((byte*)contents, data.Length)); }
            return staged;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Encode(MTLCommandBuffer batch, MTLBuffer staged, MTLTexture destination, ulong sourceRowPitch,
            in MetalTextureUpload upload)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLBlitCommandEncoder encoder = batch.BlitCommandEncoder();
            encoder.CopyFromBufferToTexture(
                staged,
                0,
                (nuint)sourceRowPitch,
                // ZERO for a 2D texture, which is what MTLCommandList.CopyTextureCore passes for anything that is
                // not a 3D texture, and this seam has no 3D texture.
                0,
                new MTLSize(upload.Width, upload.Height, 1),
                destination,
                upload.ArrayLayer,
                upload.MipLevel,
                new MTLOrigin(upload.X, upload.Y, 0));
            encoder.EndEncoding();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Commit(MTLCommandBuffer batch)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            batch.Commit();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReleaseBatch(MTLCommandBuffer batch)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            batch.Release();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReleaseStaging(MTLBuffer staged)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            staged.Release();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public MetalCommandBufferFault ReadFault(MTLCommandBuffer batch)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return MetalCommandBufferFault.Read(batch);
        }
    }
}
