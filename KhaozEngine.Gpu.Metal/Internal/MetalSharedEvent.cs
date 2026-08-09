using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DEVICE'S ONE <c>MTLSharedEvent</c>, created at device creation with an initial value of 0 and
    /// released with the device (M-F1). Everything here is a native call and nothing here decides anything,
    /// which is the split <see cref="IMetalSharedEvent"/> exists to make.
    /// <para>
    /// WHAT THIS REPLACES IS MOST OF THE POINT. The incumbent Veldrid Metal backend's fence path is a hand-built
    /// block literal and descriptor allocated with <c>Marshal.AllocHGlobal</c>, an invoke pointer from
    /// <c>Marshal.GetFunctionPointerForDelegate</c>, a lock plus a dictionary lookup INSIDE the driver's
    /// completion callback, a second process-global dictionary and static callback for AOT targets, and a
    /// <c>ManualResetEvent</c> per fence with a pooled array of them. One shared event and a non-blocking
    /// property read replace all of it, and the block that survives (M-F2) has no ordering responsibility left.
    /// </para>
    /// <para>
    /// THE SELECTORS ARE CACHED AT CONSTRUCTION rather than registered per call, which is not a micro
    /// optimisation. <c>signaledValue</c> is on the polling path that <c>RetiredResourcePool</c> hits constantly
    /// and that row 8's ring segment gate reads, and <c>sel_registerName</c> takes a C string, so registering
    /// per read would put an ASCII encode and a heap allocation on it. The selector for a given name is stable
    /// for the life of the process, so caching it is also just correct.
    /// </para>
    /// <para>
    /// <c>newSharedEvent</c> HANDS BACK A +1 OBJECT, which is what the <c>new</c> prefix means in Objective-C
    /// naming, so this type owns exactly one reference and releases it in <see cref="Dispose"/>. No autorelease
    /// pool is needed for creation for that same reason, and the caller's pool covers the message send itself.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MetalSharedEvent : IMetalSharedEvent
    {
        readonly IntPtr _handle;

        // The three selectors, resolved once. See the class note for why signaledValue in particular is not
        // registered per call.
        readonly IntPtr _signaledValue;
        readonly IntPtr _waitUntilSignaledValue;
        readonly IntPtr _encodeSignalEvent;

        bool _disposed;

        /// <summary>
        /// Create the device's shared event. Throws when the device hands back nil, which at this point in a
        /// device's life means the caller destroys the half-built device rather than running on one with no
        /// completion signal at all.
        /// </summary>
        /// <param name="device">The <c>MTLDevice</c> that outlives this event.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MetalSharedEvent(IntPtr device)
        {
            if (device == IntPtr.Zero) throw new ArgumentNullException(nameof(device));

            _handle = MetalTimelineNative.MsgSend(device, MetalTimelineNative.Sel("newSharedEvent"));
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The native Metal backend could not create the device's MTLSharedEvent, which is the one "
                    + "completion timeline every fence, every drain and the uniform ring's segment gate read. "
                    + "There is nothing to run without it, so the half-built device is destroyed rather than "
                    + "handed back.");
            }

            _signaledValue = MetalTimelineNative.Sel("signaledValue");
            _waitUntilSignaledValue = MetalTimelineNative.Sel("waitUntilSignaledValue:timeoutMS:");
            _encodeSignalEvent = MetalTimelineNative.Sel("encodeSignalEvent:value:");
        }

        /// <summary>The raw handle, for row 7's submit path and row 8's ring. Exposed by the CONCRETE type
        /// rather than by the interface, so a fake a test builds never has to invent a handle it cannot
        /// have.</summary>
        internal IntPtr Handle => _handle;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public ulong Read() => MetalTimelineNative.MsgSendULong(_handle, _signaledValue);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool WaitUntil(ulong value, uint timeoutMs)
            => MetalTimelineNative.MsgSendBoolULongUInt(_handle, _waitUntilSignaledValue, value, timeoutMs) != 0;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EncodeSignal(IntPtr commandBuffer, ulong value)
        {
            if (commandBuffer == IntPtr.Zero) throw new ArgumentNullException(nameof(commandBuffer));

            MetalTimelineNative.MsgSendVoidPtrULong(commandBuffer, _encodeSignalEvent, _handle, value);
        }

        /// <summary>
        /// Release the event, ONCE.
        /// <para>
        /// UNCONDITIONALLY, AND THAT IS THE ONE PLACE THIS TYPE DIVERGES FROM THE VULKAN SIBLING. There, the
        /// destroy is skipped after device death, because <c>vkDestroyDevice</c> has already destroyed every
        /// object made from the device and calling into the loader afterwards aborts the process. Metal has no
        /// such rule: an <c>MTLSharedEvent</c> is an ordinary reference-counted Objective-C object that outlives
        /// its device perfectly well, and skipping the release here would leak it on exactly the teardown path
        /// that matters. That is the same fact M-H3 rests on when it declines a retire list.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            MetalTimelineNative.ObjcRelease(_handle);
        }
    }
}
