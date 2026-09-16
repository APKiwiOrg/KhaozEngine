using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// A fixed set of distinct rolled items, cycled by the two resident-memory measurements. Those two ask what
/// encoded pages COST to hold, and a page blob copies the payload bytes into itself, so cycling a pool of
/// distinct items produces the same page bytes as rolling three million of them and takes a fraction of the
/// time. Every slot still takes a fresh instance id, so no entry's varint widths are shared.
/// <para>
/// It rolls through the SHIPPED <see cref="ItemGenerator"/>, which is what makes budgets 12 and the scale
/// test describe the payloads the engine actually writes. It replaced the spike's own pool when the mode
/// was re-pointed, and the measurements either side of it are unchanged.
/// </para>
/// </summary>
internal sealed class GeneratedRares
{
    GeneratedRares(ReadOnlyMemory<byte>[] payloads, int[] baseIds)
    {
        Payloads = payloads;
        BaseIds = baseIds;
    }

    /// <summary>The canonical payloads, in roll order.</summary>
    internal ReadOnlyMemory<byte>[] Payloads { get; }

    /// <summary>The base each payload was rolled on, which a slot entry carries beside it.</summary>
    internal int[] BaseIds { get; }

    /// <summary>How many distinct items the pool holds.</summary>
    internal int Count => Payloads.Length;

    /// <summary>Rolls the pool over the whole base catalog, at the item levels a bank is built from.</summary>
    internal static GeneratedRares Build(ItemGenerator generator, SyntheticContent content, IRandomSource random, int count)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(random);

        var payloads = new ReadOnlyMemory<byte>[count];
        var baseIds = new int[count];
        for (int index = 0; index < count; index++)
        {
            GenerationResult rolled = generator.Generate(new GenerationContext(
                content.BaseIdOf(random.NextInt(0, content.BaseCount)),
                random.NextInt(40, 101),
                0,
                0,
                0));
            payloads[index] = rolled.Payload;
            baseIds[index] = rolled.BaseId;
        }

        return new GeneratedRares(payloads, baseIds);
    }
}
