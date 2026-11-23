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

    private readonly SamplePayloadStream stream = new();
    private readonly SampleEncoding encoding = new();
    private readonly BinaryReader reader;
    private SampleReader? next;

    private SampleReader()
    {
        // BinaryReader requires a readable stream at construction.
        stream.Retarget(Array.Empty<byte>());
        reader = new BorrowedReader(stream, encoding);
        stream.Release();
    }

    internal static T Read<T>(Func<BinaryReader, T> read, byte[] sample)
    {
        SampleReader slot = available ?? new SampleReader();
        available = slot.next;
        slot.next = null;
        try
        {
            slot.stream.Retarget(sample);
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

    // These BinaryReader methods can return without touching a closed or empty stream.
    private sealed class BorrowedReader : BinaryReader
    {
        private readonly SamplePayloadStream stream;

        internal BorrowedReader(SamplePayloadStream stream, Encoding encoding)
            : base(stream, encoding) => this.stream = stream;

        // End this loan without permanently disposing the BinaryReader kept in the pool.
        protected override void Dispose(bool disposing)
        {
            if (disposing) stream.Release();
        }

        public override int PeekChar()
        {
            stream.EnsureOpen();
            return base.PeekChar();
        }

        public override int Read()
        {
            stream.EnsureOpen();
            return base.Read();
        }

        public override byte[] ReadBytes(int count)
        {
            stream.EnsureOpen();
            return base.ReadBytes(count);
        }

        public override char[] ReadChars(int count)
        {
            stream.EnsureOpen();
            return base.ReadChars(count);
        }

        public override int Read(char[] buffer, int index, int count)
        {
            stream.EnsureOpen();
            return base.Read(buffer, index, count);
        }

        public override int Read(Span<char> buffer)
        {
            stream.EnsureOpen();
            return base.Read(buffer);
        }

        public override void ReadExactly(Span<byte> buffer)
        {
            stream.EnsureOpen();
            base.ReadExactly(buffer);
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
