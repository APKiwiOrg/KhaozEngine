using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace KhaozEngine.Replication;

/// <summary>Reuses the BinaryReader and fixed-buffer MemoryStream that an interpolation codec reads.</summary>
internal sealed class SampleReader
{
    // A callback takes its pair out of the thread's free list. Nested decodes can reuse another pair,
    // and each thread only allocates up to its largest active decode depth.
    [ThreadStatic]
    private static SampleReader? available;

    private readonly MemoryStream stream = new(Array.Empty<byte>());
    private readonly BinaryReader reader;
    private SampleReader? next;

    private SampleReader() => reader = new BinaryReader(stream);

    internal static T Read<T>(Func<BinaryReader, T> read, byte[] sample)
    {
        SampleReader slot = available ?? new SampleReader();
        available = slot.next;
        slot.next = null;
        try
        {
            Rebind(slot.stream, sample, 0, sample.Length, writable: true, publiclyVisible: false);
            ReaderDisposed(slot.reader) = false;
            return read(slot.reader);
        }
        finally
        {
            // MemoryStream.Dispose leaves the backing array in place for ToArray, so detach the sample first.
            Rebind(slot.stream, Array.Empty<byte>(), 0, 0, writable: false, publiclyVisible: false);
            slot.stream.Dispose();
            ReaderDisposed(slot.reader) = true;
            ReaderDecoder(slot.reader)?.Reset();
            slot.next = available;
            available = slot;
        }
    }

    // BinaryReader's MemoryStream path has its own character and EOF cursor behavior. Reuse the actual
    // BCL pair and initialize the bounded stream through its array constructor to preserve those semantics.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = ".ctor")]
    private static extern void Rebind(MemoryStream stream, byte[] buffer, int index, int count, bool writable, bool publiclyVisible);

    // BinaryReader has no public reopen or decoder reset. Keep both .NET runtime accessors at this boundary.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_disposed")]
    private static extern ref bool ReaderDisposed(BinaryReader reader);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_decoder")]
    private static extern ref Decoder? ReaderDecoder(BinaryReader reader);
}
