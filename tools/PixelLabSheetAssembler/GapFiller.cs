using System.Collections.Generic;

namespace PixelLabSheetAssembler;

/// <summary>
/// Resolves, for one direction, which source frame index to draw into each of the
/// 0..frameCount-1 column slots. A missing index is filled by holding the nearest previous
/// present frame; if none precede (leading gap), the nearest following frame is held. Frames are
/// never shifted, so the row stays in sync. Each fill adds a warning. With <paramref name="strict"/>
/// the first gap throws instead. A direction with no frames always throws.
/// </summary>
public static class GapFiller
{
    public static int[] Resolve(
        string dirName, string anim, IReadOnlySet<int> present, int frameCount,
        bool strict, List<string> warnings)
    {
        if (present.Count == 0)
            throw new AssemblyException($"direction '{dirName}' has no frames for animation '{anim}'.");

        var sources = new int[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            if (present.Contains(i))
            {
                sources[i] = i;
                continue;
            }

            if (strict)
                throw new AssemblyException($"{dirName}/{anim} frame_{i:000} missing (--strict).");

            int src = -1;
            for (int j = i - 1; j >= 0; j--)
                if (present.Contains(j)) { src = j; break; }
            if (src < 0)
                for (int j = i + 1; j < frameCount; j++)
                    if (present.Contains(j)) { src = j; break; }

            sources[i] = src;
            warnings.Add($"WARNING: {dirName}/{anim} frame_{i:000} missing - held frame_{src:000}");
        }

        return sources;
    }
}
