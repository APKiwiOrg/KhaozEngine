using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Replication;

/// <summary>Reuses the BinaryReader and bounded payload stream that an interpolation codec reads.</summary>
internal sealed class SampleReader
{
    // A callback takes its pair out of the thread's free list. Nested decodes can reuse another pair,
    // and each thread only allocates up to its largest active decode depth.
    [ThreadStatic]
    private static SampleReader? available;

    private readonly FramedPayloadStream stream = new();
    private readonly SampleEncoding encoding = new();
    private readonly BinaryReader reader;
    private SampleReader? next;

    private SampleReader() => reader = new BinaryReader(stream, encoding);

    internal static T Read<T>(Func<BinaryReader, T> read, byte[] sample)
    {
        SampleReader slot = available ?? new SampleReader();
        available = slot.next;
        slot.next = null;
        try
        {
            slot.stream.Retarget(sample, 0, sample.Length);
            return read(slot.reader);
        }
        finally
        {
            slot.stream.Release();
            slot.encoding.ResetDecoder();
            slot.next = available;
            available = slot;
        }
    }

    // This encoding belongs to one reader. Keep its lazily created decoder so incomplete UTF-8
    // at the end of this sample cannot affect the next sample the reader borrows.
    private sealed class SampleEncoding : UTF8Encoding
    {
        private Decoder? decoder;

        public override Decoder GetDecoder() => decoder ??= base.GetDecoder();

        internal void ResetDecoder() => decoder?.Reset();
    }
}
