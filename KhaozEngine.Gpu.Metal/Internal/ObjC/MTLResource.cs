namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLStorageMode</c>, an <c>NSUInteger</c> and therefore <c>ulong</c>.
    /// <para>
    /// THE WIDTH IS EXACT RATHER THAN APPROXIMATE, and section 2.1 records this as one of the two enums the
    /// vendored bindings get wrong. A storage mode is a field of <c>MTLResourceOptions</c> below, which is an
    /// <c>NSUInteger</c> option set, so declaring either narrower would truncate the shifted value silently.
    /// </para>
    /// <para>
    /// DECISION M-M2 USES EXACTLY TWO OF THESE. Every buffer is <see cref="Shared"/>, reproducing the incumbent,
    /// which on unified memory means a stable <c>contents()</c> pointer both sides address. Every non-staging
    /// texture is <see cref="Private"/>, also reproducing the incumbent, and a staging texture is not a texture at
    /// all but a <see cref="Shared"/> buffer (M-C5). <c>Managed</c> is the discrete-Intel-Mac answer and is a
    /// follow-up with a named trigger rather than code, so it is absent here rather than declared and unused.
    /// </para>
    /// </summary>
    internal enum MTLStorageMode : ulong
    {
        /// <summary><c>MTLStorageModeShared</c>: one allocation both the CPU and the GPU address. Zero, which is
        /// why the incumbent's <c>newBufferWithLength:options:0</c> is a Shared buffer without saying so.</summary>
        Shared = 0,

        /// <summary><c>MTLStorageModePrivate</c>: GPU-only, with no CPU-visible pointer at all. Every texture this
        /// backend creates (M-M2).</summary>
        Private = 2,
    }

    /// <summary>
    /// <c>MTLResourceOptions</c>, the option set <c>-newBufferWithLength:options:</c> and
    /// <c>MTLTextureDescriptor.resourceOptions</c> take. An <c>NSUInteger</c>, and a PACKED one: the storage mode
    /// occupies bits 4 and up, the CPU cache mode bits 0 to 3.
    /// <para>
    /// ONLY THE ONE VALUE THIS BACKEND PASSES IS DECLARED. The incumbent passes a literal <c>0</c> for every
    /// buffer it creates, which is <c>StorageModeShared | CPUCacheModeDefaultCache</c> spelled as its numeric
    /// value, and this names it instead so a reader does not have to decode a zero. The shifted form is written
    /// out rather than folded to a constant, because the shift IS the fact a reader needs.
    /// </para>
    /// </summary>
    internal static class MTLResourceOptions
    {
        /// <summary>How far <c>MTLStorageMode</c> is shifted inside the option set
        /// (<c>MTLResourceStorageModeShift</c>).</summary>
        internal const int StorageModeShift = 4;

        /// <summary>
        /// The options every buffer in this backend is created with: <c>Shared</c> storage and the default CPU
        /// cache mode, which is numerically 0 and is what the incumbent passes.
        /// </summary>
        internal static ulong SharedDefaultCache => (ulong)MTLStorageMode.Shared << StorageModeShift;
    }
}
