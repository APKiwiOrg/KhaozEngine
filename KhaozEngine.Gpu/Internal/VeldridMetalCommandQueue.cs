using System;
using System.Reflection;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE REFLECTION M-G5 COULD NOT DELETE, ISOLATED SO IT IS ONE NAMED THING. Veldrid's
    /// <c>MTLGraphicsDevice</c> keeps its <c>id&lt;MTLCommandQueue&gt;</c> in a private field and exposes it
    /// nowhere, so a frame capture on the Veldrid Metal path has no other way to name the object to capture.
    ///
    /// <para><b>THE NATIVE METAL BACKEND DOES NOT COME THROUGH HERE AT ALL</b>, which is the whole of M-G5: it
    /// creates the queue itself and hands the pointer straight to <see cref="MetalFrameCapture.Start"/>. What
    /// this leaves is a reflection that runs only for as long as the Veldrid Metal leg ships, on a path that
    /// answers <see cref="IntPtr.Zero"/> and skips the capture rather than throwing when the layout moves.</para>
    ///
    /// <para><b>IT IS A SEPARATE TYPE SO THE FAILURE IS VISIBLE.</b> Buried inside the capture routine, a Veldrid
    /// field rename presented as "the capture wrote nothing", indistinguishable from a missing
    /// <c>MTL_CAPTURE_ENABLED</c> and from an unarmed session. As its own member it is a thing a
    /// <c>[GpuFact]</c> can ask directly on a Veldrid Metal device, which is what turns the next rename into a
    /// red test rather than an empty output directory.</para>
    /// </summary>
    internal static class VeldridMetalCommandQueue
    {
        /// <summary>The private field name this reads. Named as a constant because it is the thing that breaks,
        /// and a failure message that quotes it saves the next reader a decompile.</summary>
        internal const string FieldName = "_commandQueue";

        /// <summary>
        /// The native <c>id&lt;MTLCommandQueue&gt;</c> behind <paramref name="gd"/>, or <see cref="IntPtr.Zero"/>
        /// when it cannot be found. Zero is the answer on every non-Metal Veldrid backend as well, since none of
        /// them has that field, so the caller's own backend gate is what stops this being asked pointlessly.
        /// </summary>
        internal static IntPtr TryRead(GraphicsDevice? gd)
        {
            if (gd is null) return IntPtr.Zero;

            FieldInfo? field = gd.GetType().GetField(FieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            object? queue = field?.GetValue(gd);
            if (queue is null) return IntPtr.Zero;

            // Veldrid's MTLCommandQueue is a struct wrapping `public readonly IntPtr NativePtr;`.
            FieldInfo? nativePtr = queue.GetType().GetField("NativePtr");
            object? value = nativePtr?.GetValue(queue);
            return value is IntPtr pointer ? pointer : IntPtr.Zero;
        }
    }
}
