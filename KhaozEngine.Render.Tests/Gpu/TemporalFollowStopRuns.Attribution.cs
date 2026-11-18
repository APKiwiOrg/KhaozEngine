using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFollowStopRuns
{
    Action<int, float[], float[], bool[]>? _observe;
    internal sealed record GateCounts(int Inner, int BaseCoarse, int Lock, int Marks, int Both,
        int LockAlone, int MarksAlone, int LiveCoarse, int Mismatch);

    internal static (StopRun Walk, StopRun Control, Dictionary<int, GateCounts> Counts) TraceWorstStop()
    {
        var counts = new Dictionary<int, GateCounts>();
        var source = new TemporalFollowStopRuns(TemporalHistoryDetail.Fine);
        source._observe = (frame, classification, transport, inner) =>
        {
            int pixels = 0, baseCoarse = 0, locks = 0, marks = 0, both = 0, lockAlone = 0, marksAlone = 0;
            int liveCoarse = 0, mismatch = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                if (!inner[i]) continue;
                pixels++;
                int x = i % W, y = i / W, flags = (int)classification[i * 4 + 3], halo = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        halo |= (int)classification[(Math.Clamp(y + dy, 0, H - 1) * W
                            + Math.Clamp(x + dx, 0, W - 1)) * 4 + 3];
                float motion = classification[i * 4 + 2];
                bool locked = (flags & 32) != 0;
                bool baseline = motion >= TemporalDetailMath.StillEpsilon && ((flags | halo) & 1) == 0;
                bool lockRule = locked && motion <= TemporalResolveTuning.ShimmerStillPixels;
                bool markRule = ((flags | halo) & 2) != 0;
                if (!baseline) baseCoarse++;
                else
                {
                    if (lockRule) locks++;
                    if (markRule) marks++;
                    if (lockRule && markRule) both++;
                    if (lockRule && !markRule) lockAlone++;
                    if (markRule && !lockRule) marksAlone++;
                }
                bool fine = transport[i * 4 + 3] > 0.5f;
                if (!fine) liveCoarse++;
                if (fine != TemporalDetailMath.UseFine(motion, locked, flags, halo)) mismatch++;
            }
            counts[frame] = new(pixels, baseCoarse, locks, marks, both, lockAlone, marksAlone, liveCoarse, mismatch);
        };
        var walk = source.Perspective(FollowEnding.DampedStop, TemporalUpscale.Native, 1f,
            TemporalPerspectiveFollowGpuTests.LowPitch, FollowHeading.Away);
        var control = source.Control(walk);
        return (walk, control, counts);
    }
}
