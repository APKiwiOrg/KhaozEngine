using System;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// A fixed set of distinct generated rares, cycled by the two resident-memory measurements. Those two
/// ask what encoded pages COST to hold, and a page blob copies the payload bytes into itself, so
/// cycling a pool of distinct rares produces the same page bytes as generating three million of them
/// and takes a fraction of the time. Every slot still takes a fresh instance id, so no entry's varint
/// widths are shared.
/// </summary>
internal sealed class RarePool
{
    private RarePool(byte[][] payloads, int[] baseIds)
    {
        Payloads = payloads;
        BaseIds = baseIds;
    }

    internal byte[][] Payloads { get; }
    internal int[] BaseIds { get; }
    internal int Count => Payloads.Length;

    internal static RarePool Build(SpikeItemGenerator generator, SyntheticContent content, IRandomSource random, int count)
    {
        var payloads = new byte[count][];
        var baseIds = new int[count];
        for (int index = 0; index < count; index++)
        {
            GenerationResult generated = generator.Generate(
                new GenerationContext(random.NextInt(0, content.BaseCount), random.NextInt(40, 101), 0, 0));
            payloads[index] = generated.Payload;
            baseIds[index] = generated.BaseId;
        }

        return new RarePool(payloads, baseIds);
    }

    internal double MeanPayloadBytes()
    {
        long total = 0;
        foreach (byte[] payload in Payloads) total += payload.Length;
        return (double)total / Payloads.Length;
    }
}
