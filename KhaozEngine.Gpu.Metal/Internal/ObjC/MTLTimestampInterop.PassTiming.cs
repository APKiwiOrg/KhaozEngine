using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC;

internal sealed unsafe partial class MTLTimestampInterop
{
    internal IntPtr CreateSerialComputeDescriptor()
    {
        IntPtr descriptor = New("MTLComputePassDescriptor");
        if (descriptor != IntPtr.Zero)
            ObjCMsgSend.SendVoidNUInt(descriptor, ObjCRuntime.Sel("setDispatchType:"), (nuint)MTLDispatchType.Serial);
        return descriptor;
    }

    internal void AttachRenderSamples(IntPtr descriptor, IntPtr samples, int first)
    {
        IntPtr array = ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("sampleBufferAttachments"));
        IntPtr attachment = ObjCMsgSend.SendPtrNUInt(array, ObjCRuntime.Sel("objectAtIndexedSubscript:"), 0);
        ObjCMsgSend.SendVoidPtr(attachment, ObjCRuntime.Sel("setSampleBuffer:"), samples);
        nuint absent = nuint.MaxValue; // MTLCounterDontSample.
        ObjCMsgSend.SendVoidNUInt(attachment, ObjCRuntime.Sel("setStartOfVertexSampleIndex:"), samples == IntPtr.Zero ? absent : (nuint)first);
        ObjCMsgSend.SendVoidNUInt(attachment, ObjCRuntime.Sel("setEndOfVertexSampleIndex:"), samples == IntPtr.Zero ? absent : (nuint)(first + 1));
        ObjCMsgSend.SendVoidNUInt(attachment, ObjCRuntime.Sel("setStartOfFragmentSampleIndex:"), samples == IntPtr.Zero ? absent : (nuint)(first + 2));
        ObjCMsgSend.SendVoidNUInt(attachment, ObjCRuntime.Sel("setEndOfFragmentSampleIndex:"), samples == IntPtr.Zero ? absent : (nuint)(first + 3));
    }

    internal void ResolveOnCpu(IntPtr samples, int count, Span<ulong> values)
    {
        IntPtr data = SendResolveCpu(samples, ObjCRuntime.Sel("resolveCounterRange:"), new(0, (nuint)count));
        if (data == IntPtr.Zero || ObjCMsgSend.SendNUInt(data, ObjCRuntime.Sel("length")) < (nuint)count * sizeof(ulong))
            throw new InvalidOperationException("Metal returned no complete CPU counter result.");
        IntPtr bytes = ObjCMsgSend.Send(data, ObjCRuntime.Sel("bytes"));
        if (bytes == IntPtr.Zero) throw new InvalidOperationException("Metal returned nil counter bytes.");
        CopyResolved(bytes, (uint)count, values);
    }

    [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendResolveCpu(IntPtr receiver, IntPtr selector, TimestampRange range);
}
