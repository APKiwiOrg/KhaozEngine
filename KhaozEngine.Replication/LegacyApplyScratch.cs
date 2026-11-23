using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>One active legacy apply's readers and full-state bookkeeping.</summary>
internal sealed class LegacyApplyScratch
{
    private readonly ResettableReadEncoding encoding = new();
    private readonly ResettableReadEncoding framedEncoding = new();
    private readonly FramedPayloadStream framed = new();
    private readonly BinaryReader framedReader;

    internal LegacyApplyScratch()
    {
        Frame.Retarget(ReadOnlyMemory<byte>.Empty);
        Reader = new BinaryReader(Frame, encoding);
        Frame.Release();
        framedReader = new BinaryReader(framed, framedEncoding);
    }

    internal LegacyApplyScratch? Next { get; set; }
    internal BorrowedPayloadStream Frame { get; } = new();
    internal BinaryReader Reader { get; }
    internal long FramedWindowLength => framed.Length;
    internal HashSet<long> Seen { get; } = new();
    internal List<long> Gone { get; } = new();

    internal void DeserializeFramed(ComponentCodec codec, World world, Entity entity, ReadOnlyMemory<byte> payload)
    {
        try
        {
            framed.Retarget(payload);
            codec.Deserialize(world, entity, framedReader);
        }
        finally
        {
            framed.Release();
            framedEncoding.ResetDecoder();
        }
    }

    internal void Reset()
    {
        Frame.Release();
        encoding.ResetDecoder();
        Seen.Clear();
        Gone.Clear();
    }
}
