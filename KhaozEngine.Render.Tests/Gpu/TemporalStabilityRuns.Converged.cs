using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The fence's runs once UltraPerformance has converged. A pixel takes the display-sized reconstruction only once
    /// its carried confidence passes <c>DisplayKernelConfidenceStart</c>, and the confidence rises by the sample weight
    /// a frame, which is least at UltraPerformance: over a still view it crosses half near frame 56 there, against 25
    /// at Performance, and is whole by 120. The pans and the still camera above end at frame 79. These start their
    /// window at <see cref="ConvergedWarm"/>, at UltraPerformance alone.
    /// </summary>
    public sealed partial class TemporalStabilityRuns
    {
        /// <summary>Frames rendered before the converged window.</summary>
        public const int ConvergedWarm = 120;

        static readonly TemporalUpscale[] ConvergedPresets = { TemporalUpscale.UltraPerformance };

        StabilityPath? _convergedPan, _convergedWallPan;
        FlickerStats? _convergedStill;
        float? _convergedConfidence;

        /// <summary>The fence over the dark background at the slow pan, from <see cref="ConvergedWarm"/>.</summary>
        internal StabilityPath ConvergedFencePan => _convergedPan ??= Timed(() => Measure(
            $"fence {SlowPan:0.0} from frame {ConvergedWarm}", flatBackground: true, FenceSetup,
            (s, n) => _fence.Draw(s, n * SlowPan), _fence.Region, ConvergedWarm, Measured, ConvergedPresets));

        /// <summary>The fence over the flat wall at the slow pan, from <see cref="ConvergedWarm"/>.</summary>
        internal StabilityPath ConvergedWallPan => _convergedWallPan ??= Timed(() => Measure(
            $"fence over wall {SlowPan:0.0} from frame {ConvergedWarm}", flatBackground: true, FenceSetup,
            (s, n) => _fence.DrawOverWall(s, n * SlowPan), _fence.Region, ConvergedWarm, Measured,
            ConvergedPresets));

        /// <summary>The fence under a still camera at UltraPerformance, the last <see cref="StillMeasured"/> frames of
        /// the converged window, against one reference frame.</summary>
        internal FlickerStats ConvergedStill => _convergedStill ??= Timed(() =>
        {
            const int First = ConvergedWarm + Measured - StillMeasured;
            void Draw(Scene3D s, int _) => _fence.Draw(s, 0f);
            byte[] reference = TemporalAcceptance.Supersampled(W, H, TemporalAcceptance.SequenceReferenceFactor,
                FenceSetup(AntiAliasing.Off, TemporalUpscale.Native), Draw, First);
            return TemporalAcceptance.Flicker(TemporalAcceptance.Sequence(W, H,
                    FenceSetup(AntiAliasing.Temporal, TemporalUpscale.UltraPerformance), Draw, First, StillMeasured),
                Enumerable.Repeat(reference, StillMeasured).ToArray(), W, H, _fence.Region);
        });

        /// <summary>The median confidence the fence's region stores at UltraPerformance under the slow pan on the
        /// frame before the converged window, which the window's first frame carries.</summary>
        internal float ConvergedMedianConfidence => _convergedConfidence ??= Timed(() =>
        {
            using var fx = new TemporalFixture(W, H, FenceSetup(AntiAliasing.Temporal,
                TemporalUpscale.UltraPerformance));
            fx.Frames(ConvergedWarm, (s, n) => _fence.Draw(s, n * SlowPan));
            var history = fx.Scene.TemporalHistory;
            float[] state = TemporalTextureIo.Read(fx.Device, history.Confidence(history.WriteIndex));
            PixelRect r = _fence.Region;
            var confidence = new List<float>();
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++) confidence.Add(state[(y * W + x) * 2]);
            confidence.Sort();
            return confidence[confidence.Count / 2];
        });
    }
}
