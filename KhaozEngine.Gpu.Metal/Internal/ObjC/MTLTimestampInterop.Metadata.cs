using System;
using System.Collections.Immutable;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC;

internal sealed partial class MTLTimestampInterop : IMetalCounterMetadataNative
{
    public string? SampleBufferFailureReason { get; private set; }

    public ImmutableArray<MetalCounterSetMetadata> ReadCounterSets()
    {
        using var pool = ObjCAutoreleasePool.Enter();
        if (!MTLDiagnosticInterop.Responds(device, "counterSets")) throw new NotSupportedException("MTLDevice.counterSets is unavailable (macOS 10.15+).");
        var sets = new NSArray(ObjCMsgSend.Send(device, ObjCRuntime.Sel("counterSets")));
        return MetalCounterEnumeration.Read(new CounterReader(sets));
    }

    readonly struct CounterReader(NSArray sets) : IMetalCounterSetReader
    {
        public nuint SetCount => sets.Count();
        public string SetName(nuint set) => Name(sets.ObjectAt(set));
        NSArray Counters(nuint set) => new(ObjCMsgSend.Send(sets.ObjectAt(set), ObjCRuntime.Sel("counters")));
        public nuint CounterCount(nuint set) => Counters(set).Count();
        public string CounterName(nuint set, nuint counter) => Name(Counters(set).ObjectAt(counter));
    }

    static string Name(IntPtr value) => new NSString(ObjCMsgSend.Send(value, ObjCRuntime.Sel("name"))).ToManaged();
}
