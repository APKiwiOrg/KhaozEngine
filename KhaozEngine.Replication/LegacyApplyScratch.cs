using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>One active legacy apply's readers and full-state bookkeeping.</summary>
internal sealed class LegacyApplyScratch
{
    private readonly ResettableReadEncoding encoding = new();

    internal LegacyApplyScratch()
    {
        Frame.Retarget(ReadOnlyMemory<byte>.Empty);
        Reader = new BinaryReader(Frame, encoding);
        Frame.Release();
        FramedReader = new BinaryReader(Framed);
    }

    internal LegacyApplyScratch? Next { get; set; }
    internal BorrowedPayloadStream Frame { get; } = new();
    internal BinaryReader Reader { get; }
    internal FramedPayloadStream Framed { get; } = new();
    internal BinaryReader FramedReader { get; }
    internal HashSet<long> Seen { get; } = new();
    internal List<long> Gone { get; } = new();

    internal void Reset()
    {
        Frame.Release();
        encoding.ResetDecoder();
        Seen.Clear();
        Gone.Clear();
    }
}
