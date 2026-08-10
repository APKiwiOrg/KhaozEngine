using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE TWO NATIVE CALLS A <see cref="MetalStagingArena"/> MAKES: allocate a Shared block and release one.
    /// Everything else the arena does is arithmetic and bookkeeping, so it sits above this line where a plain
    /// <c>[Fact]</c> can reach it.
    /// <para>
    /// THE SAME SPLIT <see cref="IMetalSetupNative"/> AND <see cref="IMetalSharedEvent"/> TAKE, and for the same
    /// reason: what is left below is message sends with no policy in them, and what could be WRONG (which
    /// request lands in which size class, when a block is safe to hand back, what the retention cap keeps) runs
    /// on the Linux and Windows legs.
    /// </para>
    /// </summary>
    internal interface IMetalStagingSource
    {
        /// <summary>
        /// Allocate a Shared <c>MTLBuffer</c> of at least <paramref name="sizeBytes"/> bytes and take its
        /// <c>contents()</c> pointer. Answers an invalid block rather than throwing when the device will not
        /// allocate, and the arena is what turns that into a named refusal.
        /// </summary>
        MetalStagingBlock Create(ulong sizeBytes);

        /// <summary>
        /// Release one block's <c>MTLBuffer</c>. Called by the retention cap and by the arena's disposal, and
        /// SAFE WITH WORK IN FLIGHT: an <c>MTLCommandBuffer</c> retains every resource its encoders reference
        /// until it completes (M-H3), so this drops the arena's own reference and the driver keeps the allocation
        /// alive as long as a submitted blit still names it.
        /// </summary>
        void Destroy(in MetalStagingBlock block);
    }

    /// <summary>
    /// The real one: two message sends against the device that owns the arena's list.
    /// <para>
    /// BOTH BODIES OPEN AN AUTORELEASE POOL (M-N5). <c>-newBufferWithLength:options:</c> follows the new rule
    /// and is not autoreleased, but the class lookup underneath it and <c>-contents</c> can both produce
    /// autoreleased objects, and a record path runs on whatever thread a consumer draws on, which is exactly the
    /// thread whose implicit pool drains next never.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MetalStagingSource : IMetalStagingSource
    {
        readonly MTLDevice _device;

        /// <param name="device">The device blocks are allocated on.</param>
        internal MetalStagingSource(MTLDevice device) => _device = device;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public MetalStagingBlock Create(ulong sizeBytes)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLBuffer buffer = _device.NewBuffer((nuint)sizeBytes, MTLResourceOptions.SharedDefaultCache);
            if (buffer.IsNull) return default;

            IntPtr contents = buffer.Contents();
            if (contents == IntPtr.Zero)
            {
                buffer.Release();
                return default;
            }

            return new MetalStagingBlock(buffer.Handle, contents, sizeBytes);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Destroy(in MetalStagingBlock block)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            new MTLBuffer(block.Buffer).Release();
        }
    }
}
