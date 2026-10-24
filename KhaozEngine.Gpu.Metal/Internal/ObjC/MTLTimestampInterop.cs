using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>Metal counter selectors and their exact LP64 Objective-C ABI.</summary>
    [SupportedOSPlatform("macos")]
    internal sealed unsafe partial class MTLTimestampInterop(IntPtr device, MetalMemoryDiagnostics? memory = null) : IMetalTimestampNative
    {
        // Exported NSString constant, loaded only on the native path. The framework stays loaded process-wide.
        static class Exports
        {
            internal static readonly IntPtr TimestampName = Marshal.ReadIntPtr(NativeLibrary.GetExport(
                NativeLibrary.Load(ObjCRuntime.MetalFramework), "MTLCommonCounterSetTimestamp"));
            internal static readonly IntPtr TimestampCounterName = Marshal.ReadIntPtr(NativeLibrary.GetExport(
                NativeLibrary.Load(ObjCRuntime.MetalFramework), "MTLCommonCounterTimestamp"));
        }

        IntPtr FindCounterSet()
        {
            if (!MTLDiagnosticInterop.Responds(device, "counterSets")) throw new NotSupportedException("MTLDevice.counterSets is unavailable (macOS 10.15+).");
            var sets = new NSArray(ObjCMsgSend.Send(device, ObjCRuntime.Sel("counterSets")));
            if (sets.Count() > MetalCounterEnumeration.MaximumSets) throw new InvalidOperationException("Metal counter set enumeration exceeds its fixed bound.");
            for (nuint i = 0, count = sets.Count(); i < count; i++)
            {
                IntPtr set = sets.ObjectAt(i);
                IntPtr name = ObjCMsgSend.Send(set, ObjCRuntime.Sel("name"));
                if (ObjCMsgSend.SendBoolPtr(name, ObjCRuntime.Sel("isEqualToString:"), Exports.TimestampName) == 0) continue;
                var counters = new NSArray(ObjCMsgSend.Send(set, ObjCRuntime.Sel("counters")));
                if (counters.Count() > MetalCounterEnumeration.MaximumCountersPerSet) throw new InvalidOperationException("Metal counter enumeration exceeds its fixed bound.");
                for (nuint c = 0, n = counters.Count(); c < n; c++)
                {
                    IntPtr counterName = ObjCMsgSend.Send(counters.ObjectAt(c), ObjCRuntime.Sel("name"));
                    if (ObjCMsgSend.SendBoolPtr(counterName, ObjCRuntime.Sel("isEqualToString:"), Exports.TimestampCounterName) != 0) return set;
                }
            }
            return IntPtr.Zero;
        }

        public bool HasTimestampCounterSet => FindCounterSet() != IntPtr.Zero;
        public bool SupportsSampling(uint point)
        {
            if (!MTLDiagnosticInterop.Responds(device, "supportsCounterSampling:")) throw new NotSupportedException("MTLDevice.supportsCounterSampling: is unavailable (macOS 11+).");
            return ObjCMsgSend.SendBoolNUInt(device, ObjCRuntime.Sel("supportsCounterSampling:"), point) != 0;
        }

        public IntPtr CreateSampleBuffer(uint capacity)
        {
            IntPtr descriptor = New("MTLCounterSampleBufferDescriptor");
            if (descriptor == IntPtr.Zero) { SampleBufferFailureReason = "MTLCounterSampleBufferDescriptor could not be created."; return IntPtr.Zero; }
            try
            {
                ObjCMsgSend.SendVoidPtr(descriptor, ObjCRuntime.Sel("setCounterSet:"), FindCounterSet());
                ObjCMsgSend.SendVoidNUInt(descriptor, ObjCRuntime.Sel("setStorageMode:"), 0); // Shared, tracked.
                ObjCMsgSend.SendVoidNUInt(descriptor, ObjCRuntime.Sel("setSampleCount:"), capacity);
                IntPtr result = ObjCMsgSend.SendPtrPtrOutPtr(device,
                    ObjCRuntime.Sel("newCounterSampleBufferWithDescriptor:error:"), descriptor, out IntPtr error);
                SampleBufferFailureReason = result != IntPtr.Zero ? null : error != IntPtr.Zero
                    ? new NSError(error).LocalizedDescription() : "Metal refused the timestamp sample buffer without NSError.";
                return result;
            }
            finally { Release(descriptor); }
        }

        public IntPtr CreateBuffer(uint bytes)
        {
            IntPtr buffer = new MTLDevice(device).NewBuffer(bytes, 0).Handle;
            if (buffer == IntPtr.Zero) return buffer;
            try { memory?.OwnStaging(buffer, bytes); return buffer; }
            catch { ObjCRuntime.ObjcRelease(buffer); throw; }
        }
        public IntPtr Contents(IntPtr buffer) => new MTLBuffer(buffer).Contents();
        public IntPtr CreateEvent() => ObjCMsgSend.Send(device, ObjCRuntime.Sel("newSharedEvent"));
        public IntPtr CreateBlitDescriptor() => New("MTLBlitPassDescriptor");
        public void AttachSamples(IntPtr descriptor, IntPtr samples, ulong start, ulong end)
        {
            IntPtr attachments = ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("sampleBufferAttachments"));
            IntPtr attachment = ObjCMsgSend.SendPtrNUInt(attachments, ObjCRuntime.Sel("objectAtIndexedSubscript:"), 0);
            ObjCMsgSend.SendVoidPtr(attachment, ObjCRuntime.Sel("setSampleBuffer:"), samples);
            ObjCMsgSend.SendVoidNUInt(attachment, ObjCRuntime.Sel("setStartOfEncoderSampleIndex:"), (nuint)start);
            ObjCMsgSend.SendVoidNUInt(attachment, ObjCRuntime.Sel("setEndOfEncoderSampleIndex:"), (nuint)end);
        }

        public void Release(IntPtr handle)
        {
            memory?.ReleaseStaging(handle);
            if (handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(handle);
        }

        public void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks)
            => SendClocks(device, ObjCRuntime.Sel("sampleTimestamps:gpuTimestamp:"), out cpuNanoseconds, out gpuTicks);
        public void Signal(IntPtr commandBuffer, IntPtr ev, ulong value)
            => ObjCMsgSend.SendVoidPtrULong(commandBuffer, ObjCRuntime.Sel("encodeSignalEvent:value:"), ev, value);
        public void Wait(IntPtr commandBuffer, IntPtr ev, ulong value)
            => ObjCMsgSend.SendVoidPtrULong(commandBuffer, ObjCRuntime.Sel("encodeWaitForEvent:value:"), ev, value);
        public IntPtr BeginBlit(IntPtr commandBuffer, IntPtr descriptor)
            => descriptor == IntPtr.Zero
                ? ObjCMsgSend.Send(commandBuffer, ObjCRuntime.Sel("blitCommandEncoder"))
                : ObjCMsgSend.SendPtr(commandBuffer, ObjCRuntime.Sel("blitCommandEncoderWithDescriptor:"), descriptor);
        public void CopyMarker(IntPtr encoder, IntPtr source, IntPtr destination, uint bytes)
            => new MTLBlitCommandEncoder(encoder).CopyFromBufferToBuffer(new MTLBuffer(source), 0,
                new MTLBuffer(destination), 0, bytes);
        public void Sample(IntPtr encoder, IntPtr samples, uint index, bool barrier)
            => SendSample(encoder, ObjCRuntime.Sel("sampleCountersInBuffer:atSampleIndex:withBarrier:"),
                samples, index, barrier ? (byte)1 : (byte)0);
        public void EndBlit(IntPtr encoder) => ObjCMsgSend.SendVoid(encoder, ObjCRuntime.Sel("endEncoding"));
        public void Resolve(IntPtr encoder, IntPtr samples, uint count, IntPtr destination)
            => SendResolve(encoder, ObjCRuntime.Sel("resolveCounters:inRange:destinationBuffer:destinationOffset:"),
                samples, new TimestampRange(0, count), destination, 0);
        public void CopyResolved(IntPtr contents, uint count, Span<ulong> raw)
            => new ReadOnlySpan<ulong>((void*)contents, checked((int)count)).CopyTo(raw);

        static IntPtr New(string name) => ObjCMsgSend.Send(ObjCMsgSend.Send(ObjCRuntime.ClassNamed(name),
            ObjCRuntime.Sel("alloc")), ObjCRuntime.Sel("init"));

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct TimestampRange(nuint Location, nuint Length);

        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        internal static partial void SendClocks(IntPtr receiver, IntPtr selector, out ulong cpu, out ulong gpu);
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        internal static partial void SendSample(IntPtr receiver, IntPtr selector, IntPtr samples, nuint index, byte barrier);
        [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSend")]
        internal static partial void SendResolve(IntPtr receiver, IntPtr selector, IntPtr samples,
            TimestampRange range, IntPtr destination, nuint offset);
    }
}
