using System;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Derives the declared clipmap rebuild schedule from per-index snapped clipmap states, so a recipe that
/// moves the camera or the planes declares the events its own sequence produces.</summary>
internal static class RendererPerfClipmapEvents
{
    internal static RendererPerfClipmapSchedule Derive(long[][][] states)
    {
        int planes = states[0].Length;
        if (planes is < 1 or > 2) throw new InvalidOperationException("The clipmap schedule names one or two planes.");
        // A frame rebuilds each plane whose snapped indices differ from the previous frame's. The sequence is
        // periodic, so index 0 follows index 255 at the warm-up to measured boundary.
        int[] Changed(int index)
        {
            long[][] previous = states[RendererPerfRecipe.SequenceIndex(index - 1)], current = states[index];
            return [.. Enumerable.Range(0, planes).Select(p => current[p].AsSpan().SequenceEqual(previous[p]) ? 0 : 1)];
        }
        var events = Enumerable.Range(0, RendererPerfRecipe.Period).Select(index => (Index: index, Planes: Changed(index)))
            .Where(e => e.Planes.Sum() > 0).ToArray();
        if (events.Length != 2)
            throw new InvalidOperationException($"The clipmap schedule names an outward and a return event, not {events.Length}.");
        int first = RendererPerfRecipe.WarmupIndices[0];
        int warmup = events.Where(e => e.Index > first).Sum(e => e.Planes.Sum());
        int measured = events.Sum(e => e.Planes.Sum());
        return new(Event(events[0]), Event(events[1]), planes, warmup, measured);

        static RendererPerfClipmapEvent Event((int Index, int[] Planes) e) =>
            new(e.Index, e.Planes[0], e.Planes.Length > 1 ? e.Planes[1] : 0);
    }
}
