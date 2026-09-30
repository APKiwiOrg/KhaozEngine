using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One camera path under every mode measured, each against the path's supersampled reference
    /// sequence over the same region and window. <see cref="Lagged"/> is the reference sequence one frame late, the
    /// control an output that tracks the reference exactly but late scores. <see cref="FlickerStats.Energy"/> takes
    /// its background at pixel (2, 2), so it means something only where <see cref="FlatBackground"/> says that pixel
    /// is flat background.</summary>
    internal sealed record StabilityPath(string Name, bool FlatBackground,
        IReadOnlyDictionary<TemporalUpscale, FlickerStats> Temporal, FlickerStats Msaa, FlickerStats Off,
        FlickerStats Lagged, double Seconds)
    {
        /// <summary>The error of a frozen image: the reference sequence's own change.</summary>
        public double Frozen => Off.ReferenceChange;
    }

    /// <summary>A prop held at half dissolve under a still camera. A coverage is the share of the way the prop's
    /// mean luma sits from the bare wall to the solid prop, under temporal anti-aliasing (<see cref="Coverage"/>), in
    /// the supersampled reference (<see cref="ReferenceCoverage"/>) and without anti-aliasing
    /// (<see cref="OffCoverage"/>). <see cref="Flips"/> and <see cref="OwnChange"/> are the temporal frames' raw flips
    /// and own change per pixel per frame over the held frames.</summary>
    internal readonly record struct HeldDissolve(double Coverage, double ReferenceCoverage, double OffCoverage,
        double Flips, double OwnChange);

    /// <summary>
    /// The runs the temporal stability acceptance reads, rendered on first use and kept for the whole test class,
    /// because the printed table and every assertion read the same runs. Each camera path renders its supersampled
    /// reference sequence once and each mode once, and only the measures are kept, not the frames.
    /// <para>
    /// HDR is off in every run, the legacy chain with no tonemap, so the reference's box filter, the MSAA resolve and
    /// the temporal resolve average the same display values (the resolve design's amendment 21). The sharpen stays at
    /// its shipped default of 0.25.
    /// </para>
    /// <para>
    /// By default only the runs the assertions read are rendered: the slow pan over the background at every preset,
    /// the slow pan over the wall, the still fence and the zoom at Native and Quality, and the prop paths at Native,
    /// each with MSAA 4x, no anti-aliasing and the lag control where a path has them. With
    /// <see cref="TableVariable"/> set to 1 every run of the motion-clarity table is rendered as well: every preset,
    /// the pans at 0.5 and 1.0 display pixels a frame, and the prop paths at Quality.
    /// </para>
    /// </summary>
    public sealed partial class TemporalStabilityRuns
    {
        public const int W = 320, H = 180;

        /// <summary>Frames rendered before the window and frames measured in it, on every pan.</summary>
        public const int Warm = 16, Measured = 64;

        /// <summary>The zoom's window: one sine cycle straight after its hold.</summary>
        public const int ZoomMeasured = IsoYard.CycleFrames;

        /// <summary>The still camera's window is the pan window's last frames, 64 to 79.</summary>
        public const int StillMeasured = 16;

        /// <summary>The held dissolve's frames: converging, then measured.</summary>
        public const int HeldWarm = 32, HeldMeasured = 16;

        /// <summary>The slow pan, in display pixels a frame.</summary>
        public const float SlowPan = 0.2f;

        /// <summary>The pan speeds the table covers, in display pixels a frame.</summary>
        internal static readonly float[] Speeds = { SlowPan, 0.5f, 1f };

        internal static readonly TemporalUpscale[] Presets = Enum.GetValues<TemporalUpscale>();

        /// <summary>The presets the assertions read, beside the slow pan's shimmer guard, which reads every
        /// preset.</summary>
        internal static readonly TemporalUpscale[] AssertedPresets =
            { TemporalUpscale.Native, TemporalUpscale.Quality };

        /// <summary>The environment variable that turns on the full motion-clarity table when it is 1.</summary>
        public const string TableVariable = "KE_TEMPORAL_ACCEPTANCE_TABLE";

        /// <summary>Whether this run renders the full motion-clarity table, not only the asserted runs.</summary>
        internal static bool FullTable => Environment.GetEnvironmentVariable(TableVariable) == "1";

        /// <summary>The pan speeds this run renders: every speed of the table, or the slow pan alone.</summary>
        internal static IReadOnlyList<float> TableSpeeds => FullTable ? Speeds : new[] { SlowPan };

        // Every preset for the table, or the asserted ones alone.
        static IReadOnlyList<TemporalUpscale> TablePresets => FullTable ? Presets : AssertedPresets;

        // The prop paths assert at Native and report Quality in the table.
        static IReadOnlyList<TemporalUpscale> PropPresets =>
            FullTable ? AssertedPresets : new[] { TemporalUpscale.Native };

        /// <summary>The prop mid-fade, half dissolved.</summary>
        public const float HalfDissolve = 0.5f;

        readonly FenceScene _fence = new(W, H);
        readonly IsoYard _yard = new(W, H);
        readonly FadeProp _prop = new(W, H);
        readonly Dictionary<float, StabilityPath> _fencePans = new(), _wallPans = new();
        readonly Dictionary<bool, StabilityPath> _propPans = new();
        IReadOnlyDictionary<TemporalUpscale, FlickerStats>? _still;
        StabilityPath? _zoom;
        HeldDissolve? _held;

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        internal static PixelRect ZoomRegion => new(W / 10, H / 10, W * 9 / 10, H * 9 / 10);

        /// <summary>The pixels the prop covers at every pan of the window, frame 0 to the last.</summary>
        internal PixelRect PropRegion => _prop.Region((Warm + Measured) * FadeProp.PanPixelsPerFrame);

        /// <summary>The fence over the dark background panning <paramref name="speed"/> display pixels a frame, at
        /// every preset.</summary>
        internal StabilityPath FencePan(float speed) => Cached(_fencePans, speed, () => Measure($"fence {speed:0.0}",
            flatBackground: true, FenceSetup, (s, n) => _fence.Draw(s, n * speed), _fence.Region, Warm, Measured,
            Presets));

        /// <summary>The fence over the flat wall panning <paramref name="speed"/> display pixels a frame.</summary>
        internal StabilityPath WallPan(float speed) => Cached(_wallPans, speed, () => Measure(
            $"fence over wall {speed:0.0}", flatBackground: true, FenceSetup,
            (s, n) => _fence.DrawOverWall(s, n * speed), _fence.Region, Warm, Measured, TablePresets));

        /// <summary>The engine's default isometric camera zooming over the yard, from the end of its hold. Pixel
        /// (2, 2) is not flat background there.</summary>
        internal StabilityPath Zoom => _zoom ??= Timed(() => Measure("isometric zoom", flatBackground: false,
            YardSetup, _yard.Draw, ZoomRegion, IsoYard.HoldFrames, ZoomMeasured, TablePresets));

        /// <summary>The prop at half dissolve, or as a half LOD crossfade, under the slow pan. Its energy is not
        /// read.</summary>
        internal StabilityPath PropPan(bool crossfade) => Cached(_propPans, crossfade, () => Measure(
            crossfade ? "LOD crossfade" : "half dissolve", flatBackground: false, PropSetup,
            (s, n) => _prop.Draw(s, n * FadeProp.PanPixelsPerFrame, HalfDissolve, crossfade), PropRegion, Warm,
            Measured, PropPresets));

        /// <summary>The fence under a still camera, frames 64 to 79, against one reference frame, since nothing in
        /// the reference moves.</summary>
        internal IReadOnlyDictionary<TemporalUpscale, FlickerStats> Still => _still ??= Timed(() =>
        {
            const int First = Warm + Measured - StillMeasured;
            void Draw(Scene3D s, int _) => _fence.Draw(s, 0f);
            byte[] reference = TemporalAcceptance.Supersampled(W, H, TemporalAcceptance.SequenceReferenceFactor,
                FenceSetup(AntiAliasing.Off, TemporalUpscale.Native), Draw, First);
            byte[][] references = Enumerable.Repeat(reference, StillMeasured).ToArray();
            var still = new Dictionary<TemporalUpscale, FlickerStats>();
            foreach (TemporalUpscale preset in TablePresets)
                still[preset] = TemporalAcceptance.Flicker(TemporalAcceptance.Sequence(W, H,
                    FenceSetup(AntiAliasing.Temporal, preset), Draw, First, StillMeasured), references, W, H,
                    _fence.Region);
            return still;
        });

        /// <summary>The prop held at half dissolve under temporal anti-aliasing at Native with a still camera. Its
        /// coverage compares the last held frame with a converged solid prop and a converged bare wall. The reference
        /// and no anti-aliasing coverages compare the same three draws at that last frame.</summary>
        internal HeldDissolve Held => _held ??= Timed(() =>
        {
            PixelRect region = PropRegion;
            const int Last = HeldWarm + HeldMeasured - 1;
            void Half(Scene3D s, int _) => _prop.Draw(s, 0f, HalfDissolve, crossfade: false);
            void Solid(Scene3D s, int _) => _prop.Draw(s, 0f, 0f, crossfade: false);
            void Absent(Scene3D s, int _) => _prop.Background(s, 0f);
            double Coverage(byte[] half, byte[] solid, byte[] absent)
            {
                double lAbsent = TemporalAcceptance.MeanLuma(absent, W, region);
                return (TemporalAcceptance.MeanLuma(half, W, region) - lAbsent)
                    / (TemporalAcceptance.MeanLuma(solid, W, region) - lAbsent);
            }

            Action<Scene3D> taa = PropSetup(AntiAliasing.Temporal, TemporalUpscale.Native);
            byte[][] held = TemporalAcceptance.Sequence(W, H, taa, Half, HeldWarm, HeldMeasured);
            var flips = new FlipCounter(W, H, region);
            foreach (byte[] frame in held) flips.Add(frame);
            double coverage = Coverage(held[^1], TemporalAcceptance.Sequence(W, H, taa, Solid, HeldWarm - 1, 1)[0],
                TemporalAcceptance.Sequence(W, H, taa, Absent, HeldWarm - 1, 1)[0]);

            Action<Scene3D> off = PropSetup(AntiAliasing.Off, TemporalUpscale.Native);
            byte[] Reference(Action<Scene3D, int> draw) => TemporalAcceptance.Supersampled(W, H,
                TemporalAcceptance.SequenceReferenceFactor, off, draw, Last);
            byte[] Aliased(Action<Scene3D, int> draw) => Frames(off, AntiAliasing.Off, draw, Last, 1)[0];
            return new HeldDissolve(coverage, Coverage(Reference(Half), Reference(Solid), Reference(Absent)),
                Coverage(Aliased(Half), Aliased(Solid), Aliased(Absent)), flips.FlipsPerPixelPerFrame,
                TemporalAcceptance.OwnChange(held, W, H, region));
        });

        Action<Scene3D> FenceSetup(AntiAliasing aa, TemporalUpscale preset) => s =>
        {
            _fence.Stage.Setup(s, aa, preset);
            s.Post.Hdr.Enabled = false;
        };

        Action<Scene3D> YardSetup(AntiAliasing aa, TemporalUpscale preset) => s =>
        {
            _yard.Setup(s, aa);
            s.Post.Temporal.Upscale = preset;
            s.Post.Hdr.Enabled = false;
        };

        Action<Scene3D> PropSetup(AntiAliasing aa, TemporalUpscale preset) => s =>
        {
            _prop.Stage.Setup(s, aa, preset);
            s.Post.Hdr.Enabled = false;
        };

        StabilityPath Cached<TKey>(Dictionary<TKey, StabilityPath> cache, TKey key, Func<StabilityPath> measure)
            where TKey : notnull
        {
            if (!cache.TryGetValue(key, out StabilityPath? path)) cache[key] = path = Timed(measure);
            return path;
        }

        T Timed<T>(Func<T> measure)
        {
            long started = Stopwatch.GetTimestamp();
            T result = measure();
            Seconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
            return result;
        }

        // Frames first to first plus count minus 1 of a fresh fixture. Only temporal anti-aliasing carries anything
        // from one frame to the next, so under any other mode the frames before the window are begun, not rendered.
        static byte[][] Frames(Action<Scene3D> setup, AntiAliasing aa, Action<Scene3D, int> draw, int first, int count)
        {
            if (aa == AntiAliasing.Temporal) return TemporalAcceptance.Sequence(W, H, setup, draw, first, count);
            using var fx = new TemporalFixture(W, H, setup);
            fx.SkipFrames(first);
            var frames = new byte[count][];
            for (int i = 0; i < count; i++) frames[i] = fx.Frame(draw);
            return frames;
        }

        // The reference sequence starts one frame before the window, so the frame before it feeds the lag control.
        static StabilityPath Measure(string name, bool flatBackground,
            Func<AntiAliasing, TemporalUpscale, Action<Scene3D>> setup, Action<Scene3D, int> draw, PixelRect region,
            int warm, int count, IReadOnlyList<TemporalUpscale> presets)
        {
            long started = Stopwatch.GetTimestamp();
            byte[][] all = TemporalAcceptance.ReferenceSequence(W, H, setup(AntiAliasing.Off, TemporalUpscale.Native),
                draw, warm - 1, count + 1);
            byte[][] reference = all[1..];
            byte[][] lagged = TemporalAcceptance.LaggedReference(reference, all[0]);
            FlickerStats Run(AntiAliasing aa, TemporalUpscale preset) => TemporalAcceptance.Flicker(
                Frames(setup(aa, preset), aa, draw, warm, count), reference, W, H, region);
            var temporal = new Dictionary<TemporalUpscale, FlickerStats>();
            foreach (TemporalUpscale preset in presets) temporal[preset] = Run(AntiAliasing.Temporal, preset);
            FlickerStats msaa = Run(AntiAliasing.Msaa(4), TemporalUpscale.Native);
            FlickerStats off = Run(AntiAliasing.Off, TemporalUpscale.Native);
            FlickerStats late = TemporalAcceptance.Flicker(lagged, reference, W, H, region);
            return new StabilityPath(name, flatBackground, temporal, msaa, off, late,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }
}
