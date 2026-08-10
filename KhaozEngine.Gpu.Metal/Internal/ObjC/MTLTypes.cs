using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLSize</c>: three <c>NSUInteger</c>s, the extent of a copy region.
    /// <para>
    /// TWENTY-FOUR BYTES OF INTEGERS, WHICH DECIDES HOW IT CROSSES. The arm64 rule row 1's spike measured is that
    /// a homogeneous FLOATING-POINT aggregate of at most four members rides the vector registers and everything
    /// else over sixteen bytes is passed INDIRECTLY, as a pointer the caller supplies. This is not floating point
    /// at all, so it takes the indirect path for the same reason <c>MTLScissorRect</c> does, which is the shape
    /// the spike measured under that arm (section 3.1).
    /// </para>
    /// </summary>
    /// <param name="Width">Extent on X, in texels for a texture copy.</param>
    /// <param name="Height">Extent on Y.</param>
    /// <param name="Depth">Extent on Z, which is 1 for every texture the GPU seam can express.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLSize(nuint Width, nuint Height, nuint Depth);

    /// <summary>
    /// <c>MTLOrigin</c>: three <c>NSUInteger</c>s, the corner a copy region starts at. Same size, same layout and
    /// therefore the same ABI class as <see cref="MTLSize"/>.
    /// </summary>
    /// <param name="X">Left edge.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Z">Depth slice, which is 0 for every texture the GPU seam can express.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLOrigin(nuint X, nuint Y, nuint Z);
}
