using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 5: the frame after <see cref="Scene3D.CameraCut"/>, a resize or
    /// a preset change matches a from-scratch render, and so does the frame after every other reset.
    /// <para>
    /// A from-scratch render is the first temporal frame of a fresh scene set up with the settings in effect after the
    /// change. It begins the same number of frames without rendering them (<see cref="TemporalFixture.SkipFrames"/>),
    /// so both frames share one frame index, jitter phase and offset, effect clock and draw callback number, and they
    /// draw the same scene from the same camera. Each case asserts that alignment on the two frames' diagnostics. With
    /// history invalid in both, the resolve takes the current frame alone, so the two must agree to rounding.
    /// </para>
    /// <para>
    /// Each case also asserts the reason the reset reports, which follows the precedence FirstFrame, DeviceReset,
    /// AntiAliasing, RenderScale, Resize, CameraCutRequested, CameraCutDetected when several triggers land on one
    /// frame, that history is invalid on the reset frame, and that it is valid again on the next with the same
    /// reason kept. The image alone cannot see a missing reset where the resolve would not have read history anyway,
    /// such as a 20 m cut, which moves every pixel's history off screen. Two settings that do not reset are checked
    /// the other way: a sharpness or mip bias offset change keeps history.
    /// </para>
    /// <para>
    /// HDR is off in every case but the three that toggle it. Nothing is compared with a stored image, and every
    /// measured value is printed.
    /// </para>
    /// </summary>
    public sealed class TemporalResetGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, BigW = 480, BigH = 270;

        // The render cap of the case whose display resize keeps the internal size: Quality gives 213 by 120 at 320 by
        // 180 and 320 by 180 at 480 by 270, and both fit to 192 by 108.
        const int CapW = 192, CapH = 108;

        // Frames rendered before the change. The change lands on the frame after.
        const int Before = 20;

        // Rounding between two renders of the same frame, in 8-bit steps: the worst channel and the mean over every
        // colour channel. Measured 0 and 0 in every case on Metal.
        const int MaxWorstStep = 1;
        const double MaxMeanStep = 0.05;

        // A frame that kept its history differs from the from-scratch render by at least this mean, in 8-bit steps,
        // and so does a from-scratch render at another mip bias offset. Measured 17.4 after a sharpness change, 13.2
        // after a mip bias offset change, and 14.4 between the two offsets.
        const double MinKeptHistoryMeanStep = 1;

        /// <summary>The change each reset case makes between two frames.</summary>
        public enum ResetCase
        {
            CameraCut,
            AutomaticCut,
            CameraCutPastTheCutDistance,
            DisplayResize,
            DisplayResizeUnderTheCap,
            DisplayResizeWithCameraCut,
            PresetToPerformance,
            PresetToUltraPerformance,
            PresetChangeWithDisplayResize,
            HdrOn,
            HdrOff,
            HdrToggleWithPresetResizeAndCut,
            TemporalTurnedOn,
            AntiAliasingChangeUnderTheMotionView,
        }

        enum InternalSize { Any, Held, Moves }

        /// <summary>The settings one side of a change renders with.</summary>
        readonly record struct Look(int Width, int Height, AntiAliasingMode Mode, TemporalUpscale Preset, bool Hdr,
            bool MotionView = false, bool Capped = false);

        /// <summary>A reset case: the settings before and after, whether <see cref="Scene3D.CameraCut"/> is called,
        /// where the camera goes, the reason expected, and what the change does to the internal size.</summary>
        sealed record Change(TemporalResetReason Expected, Look From, Look To, bool CutCall = false,
            float CameraTo = 0f, InternalSize Internal = InternalSize.Any)
        {
            // A cut, called or detected, lands on a different arrangement of the scene.
            public bool NewLayout => CutCall || CameraTo != 0f;
        }

        static readonly Look Native = new(W, H, AntiAliasingMode.Temporal, TemporalUpscale.Native, Hdr: false);
        static readonly Look Quality = Native with { Preset = TemporalUpscale.Quality };
        static readonly Look Big = Quality with { Width = BigW, Height = BigH };

        static Change Describe(ResetCase which) => which switch
        {
            // 6 m, inside the automatic cut distance of 16 m.
            ResetCase.CameraCut => new(TemporalResetReason.CameraCutRequested, Native, Native, CutCall: true,
                CameraTo: 6f),
            ResetCase.AutomaticCut => new(TemporalResetReason.CameraCutDetected, Native, Native, CameraTo: 20f),
            ResetCase.CameraCutPastTheCutDistance => new(TemporalResetReason.CameraCutRequested, Native, Native,
                CutCall: true, CameraTo: 20f),
            ResetCase.DisplayResize => new(TemporalResetReason.Resize, Quality, Big, Internal: InternalSize.Moves),
            ResetCase.DisplayResizeUnderTheCap => new(TemporalResetReason.Resize, Quality with { Capped = true },
                Big with { Capped = true }, Internal: InternalSize.Held),
            ResetCase.DisplayResizeWithCameraCut => new(TemporalResetReason.Resize, Quality, Big, CutCall: true,
                CameraTo: 6f),
            // The internal size moves with the preset, and the preset outranks the size change it brings.
            ResetCase.PresetToPerformance => new(TemporalResetReason.RenderScale, Quality,
                Quality with { Preset = TemporalUpscale.Performance }, Internal: InternalSize.Moves),
            // A third per axis, 107 by 60 here, with a 72-phase jitter cycle.
            ResetCase.PresetToUltraPerformance => new(TemporalResetReason.RenderScale, Quality,
                Quality with { Preset = TemporalUpscale.UltraPerformance }, Internal: InternalSize.Moves),
            ResetCase.PresetChangeWithDisplayResize => new(TemporalResetReason.RenderScale, Quality,
                Big with { Preset = TemporalUpscale.Performance }),
            ResetCase.HdrOn => new(TemporalResetReason.DeviceReset, Quality, Quality with { Hdr = true }),
            ResetCase.HdrOff => new(TemporalResetReason.DeviceReset, Quality with { Hdr = true }, Quality),
            ResetCase.HdrToggleWithPresetResizeAndCut => new(TemporalResetReason.DeviceReset,
                Quality with { Hdr = true }, Big with { Preset = TemporalUpscale.Performance }, CutCall: true,
                CameraTo: 6f),
            // Temporal rendering was off, so this is its first frame whatever else changed.
            ResetCase.TemporalTurnedOn => new(TemporalResetReason.FirstFrame,
                Quality with { Mode = AntiAliasingMode.None }, Quality with { Hdr = true }, CutCall: true,
                CameraTo: 6f),
            // The motion vectors view keeps temporal rendering on, so the selection change is the reason, over the
            // render scale and internal size it also changes.
            ResetCase.AntiAliasingChangeUnderTheMotionView => new(TemporalResetReason.AntiAliasing,
                Quality with { MotionView = true }, Quality with { Mode = AntiAliasingMode.Fxaa, MotionView = true }),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, null),
        };

        static void Apply(Scene3D s, Look look)
        {
            s.Post.Quality.AntiAliasing = look.Mode switch
            {
                AntiAliasingMode.Temporal => AntiAliasing.Temporal,
                AntiAliasingMode.Fxaa => AntiAliasing.Fxaa,
                _ => AntiAliasing.Off,
            };
            s.Post.Temporal.Upscale = look.Preset;
            s.Post.Hdr.Enabled = look.Hdr;
            s.DebugView = look.MotionView ? SceneDebugView.MotionVectors : SceneDebugView.None;
            if (!look.Capped) return;
            s.Post.MaxRenderWidth = CapW;
            s.Post.MaxRenderHeight = CapH;
        }

        // The stage keeps a 16:9 aspect, so the camera frames the same view at both display sizes.
        static Action<Scene3D> Setup(FrontStage stage, Look look) => s =>
        {
            stage.Setup(s, AntiAliasing.Temporal);
            Apply(s, look);
        };

        /// <summary>
        /// The scene: the noise-textured panel at half scale, one texel to a display pixel at 320 by 180, following the
        /// camera, a keyed body and an unkeyed pillar in front of it. The body drifts right 0.8 display pixels a frame
        /// in the first arrangement. <paramref name="newLayout"/> is the arrangement after a cut, a different body and
        /// pillar place and colour.
        /// </summary>
        static void Layout(FrontStage stage, Scene3D s, int n, float cameraX, bool newLayout)
        {
            s.Camera.Target = new Vector3(cameraX, 0f, 0f);
            s.Draw(stage.Panel, Matrix4x4.CreateScale(0.5f, 0.5f, 1f) * Matrix4x4.CreateTranslation(cameraX, 0f, 0f));
            Vector3 body = newLayout ? new(cameraX + 1f, -0.5f, 0f) : new(cameraX - 1.5f + 0.02f * n, 0.4f, 0f);
            s.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateScale(0.9f) * Matrix4x4.CreateTranslation(body))
            {
                Tint = newLayout ? new Color(0.2f, 0.4f, 0.9f, 1f) : new Color(0.9f, 0.3f, 0.2f, 1f),
                Motion = MotionKey.From(51),
            });
            s.Draw(stage.Box, Matrix4x4.CreateScale(0.3f, 2f, 0.3f)
                * Matrix4x4.CreateTranslation(cameraX + (newLayout ? -2f : 2f), 0f, 0f),
                new Color(0.9f, 0.9f, 0.85f, 1f));
        }

        /// <summary>The worst colour channel difference in 8-bit steps, its mean over every colour channel, and the
        /// pixels with any colour channel differing.</summary>
        static (int Worst, double Mean, int Pixels) Compare(byte[] a, byte[] b)
        {
            Assert.Equal(b.Length, a.Length);
            int worst = 0, pixels = 0;
            long sum = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                int r = Math.Abs(a[i] - b[i]), g = Math.Abs(a[i + 1] - b[i + 1]), bl = Math.Abs(a[i + 2] - b[i + 2]);
                int d = Math.Max(r, Math.Max(g, bl));
                sum += r + g + bl;
                worst = Math.Max(worst, d);
                if (d > 0) pixels++;
            }
            return (worst, sum / (3.0 * (a.Length / 4)), pixels);
        }

        static string Describe(TemporalDiagnostics d) =>
            $"frame {d.FrameIndex}, phase {d.JitterPhase}, jitter {d.JitterPixels.X:0.0000},{d.JitterPixels.Y:0.0000}, "
            + $"history valid {d.HistoryValid}, last reset {d.LastReset}, "
            + $"internal {d.InternalWidth}x{d.InternalHeight}, display {d.DisplayWidth}x{d.DisplayHeight}, "
            + $"{d.Preset}, ratio {d.UpscaleRatio:0.0000}";

        [GpuTheory]
        [InlineData(ResetCase.CameraCut)]
        [InlineData(ResetCase.AutomaticCut)]
        [InlineData(ResetCase.CameraCutPastTheCutDistance)]
        [InlineData(ResetCase.DisplayResize)]
        [InlineData(ResetCase.DisplayResizeUnderTheCap)]
        [InlineData(ResetCase.DisplayResizeWithCameraCut)]
        [InlineData(ResetCase.PresetToPerformance)]
        [InlineData(ResetCase.PresetToUltraPerformance)]
        [InlineData(ResetCase.PresetChangeWithDisplayResize)]
        [InlineData(ResetCase.HdrOn)]
        [InlineData(ResetCase.HdrOff)]
        [InlineData(ResetCase.HdrToggleWithPresetResizeAndCut)]
        [InlineData(ResetCase.TemporalTurnedOn)]
        [InlineData(ResetCase.AntiAliasingChangeUnderTheMotionView)]
        public void TheFrameAfterAResetMatchesAFromScratchRender(ResetCase which)
        {
            Change c = Describe(which);
            var stage = new FrontStage(W, H, 4.5f);
            void First(Scene3D s, int n) => Layout(stage, s, n, 0f, false);
            void After(Scene3D s, int n) => Layout(stage, s, n, c.CameraTo, c.NewLayout);

            byte[] reset, scratch;
            TemporalDiagnostics before, onReset, next, fresh;
            using (var a = new TemporalFixture(c.From.Width, c.From.Height, Setup(stage, c.From)))
            {
                a.Frames(Before, First);
                before = a.Scene.LastTemporalDiagnostics;
                if (c.To.Width != c.From.Width || c.To.Height != c.From.Height) a.Resize(c.To.Width, c.To.Height);
                Apply(a.Scene, c.To);
                if (c.CutCall) a.Scene.CameraCut();
                reset = a.Frame(After);
                onReset = a.Scene.LastTemporalDiagnostics;
                a.Frames(1, After);
                next = a.Scene.LastTemporalDiagnostics;
            }
            using (var b = new TemporalFixture(c.To.Width, c.To.Height, Setup(stage, c.To)))
            {
                b.SkipFrames(Before);
                scratch = b.Frame(After);
                fresh = b.Scene.LastTemporalDiagnostics;
            }

            var (worst, mean, pixels) = Compare(reset, scratch);
            output.WriteLine($"{which}, expecting {c.Expected}");
            output.WriteLine($"  before the change: {Describe(before)}");
            output.WriteLine($"  reset frame:       {Describe(onReset)}");
            output.WriteLine($"  next frame:        {Describe(next)}");
            output.WriteLine($"  from scratch:      {Describe(fresh)}");
            output.WriteLine($"  against from scratch: worst {worst} steps, mean {mean:0.0000}, {pixels} pixels "
                + "differ");

            Assert.Equal(c.Expected, onReset.LastReset);
            Assert.False(onReset.HistoryValid, "history must be invalid on the reset frame");
            Assert.True(next.HistoryValid, "history must be valid again on the frame after the reset");
            Assert.Equal(c.Expected, next.LastReset);

            // The two frames are aligned: one frame index, jitter phase and offset, size and preset.
            Assert.Equal(TemporalResetReason.FirstFrame, fresh.LastReset);
            Assert.False(fresh.HistoryValid);
            Assert.Equal(fresh.FrameIndex, onReset.FrameIndex);
            Assert.Equal(fresh.JitterPhase, onReset.JitterPhase);
            Assert.Equal(fresh.JitterPixels, onReset.JitterPixels);
            Assert.Equal((fresh.InternalWidth, fresh.InternalHeight), (onReset.InternalWidth, onReset.InternalHeight));
            Assert.Equal((fresh.DisplayWidth, fresh.DisplayHeight), (onReset.DisplayWidth, onReset.DisplayHeight));
            Assert.Equal(fresh.Preset, onReset.Preset);
            Assert.Equal(fresh.UpscaleRatio, onReset.UpscaleRatio);

            var sizeBefore = (before.InternalWidth, before.InternalHeight);
            var sizeAfter = (onReset.InternalWidth, onReset.InternalHeight);
            if (c.Internal == InternalSize.Held)
                Assert.True(sizeBefore == sizeAfter, $"the internal size must hold, {sizeBefore} became {sizeAfter}");
            if (c.Internal == InternalSize.Moves)
                Assert.True(sizeBefore != sizeAfter, $"the internal size must move, it stayed {sizeAfter}");

            Assert.True(worst <= MaxWorstStep && mean <= MaxMeanStep, $"{which}: the frame after the reset differs "
                + $"from a from-scratch render (worst {worst} steps, mean {mean:0.0000}, {pixels} pixels)");
        }

        /// <summary>
        /// A sharpness change keeps history, and touches nothing but the sharpen after the resolve: the frame equals
        /// the same frame of a run with the new sharpness throughout, and differs from a from-scratch render.
        /// </summary>
        [GpuFact]
        public void ASharpnessChangeKeepsHistory()
        {
            var stage = new FrontStage(W, H, 4.5f);
            void Draw(Scene3D s, int n) => Layout(stage, s, n, 0f, false);
            Action<Scene3D> setup = Setup(stage, Quality);
            byte[] changed, throughout, scratch;
            TemporalDiagnostics d;
            using (var a = new TemporalFixture(W, H, setup))
            {
                a.Frames(Before, Draw);
                a.Scene.Post.Temporal.Sharpness = 1f;
                changed = a.Frame(Draw);
                d = a.Scene.LastTemporalDiagnostics;
            }
            using (var c = new TemporalFixture(W, H, s => { setup(s); s.Post.Temporal.Sharpness = 1f; }))
            {
                c.Frames(Before, Draw);
                throughout = c.Frame(Draw);
            }
            using (var b = new TemporalFixture(W, H, s => { setup(s); s.Post.Temporal.Sharpness = 1f; }))
            {
                b.SkipFrames(Before);
                scratch = b.Frame(Draw);
            }
            var same = Compare(changed, throughout);
            var kept = Compare(changed, scratch);
            output.WriteLine($"sharpness 0.25 to 1: {Describe(d)}");
            output.WriteLine($"  against a run at 1 throughout: worst {same.Worst} steps, mean {same.Mean:0.0000}");
            output.WriteLine($"  against from scratch: worst {kept.Worst} steps, mean {kept.Mean:0.0000}");

            Assert.True(d.HistoryValid, "a sharpness change must keep history");
            Assert.Equal(TemporalResetReason.FirstFrame, d.LastReset);
            Assert.True(same.Worst <= MaxWorstStep && same.Mean <= MaxMeanStep, "the frame differs from a run at the "
                + $"new sharpness throughout (worst {same.Worst}, mean {same.Mean:0.0000})");
            Assert.True(kept.Mean >= MinKeptHistoryMeanStep,
                $"the frame is too close to a from-scratch render to have kept history (mean {kept.Mean:0.0000})");
        }

        /// <summary>
        /// A mip bias offset change keeps history: the frame differs from a from-scratch render at the new offset. The
        /// change takes effect, because the panel's texels are a display pixel across, so at Quality the default
        /// offset samples the top mip and the largest offset, 1, the next one down: two from-scratch renders, one at
        /// each offset, differ.
        /// </summary>
        [GpuFact]
        public void AMipBiasOffsetChangeKeepsHistory()
        {
            var stage = new FrontStage(W, H, 4.5f);
            void Draw(Scene3D s, int n) => Layout(stage, s, n, 0f, false);
            Action<Scene3D> setup = Setup(stage, Quality);
            Action<Scene3D> biased = s => { setup(s); s.Post.Temporal.MipBiasOffset = 1f; };
            byte[] changed, scratch, scratchAtDefault;
            TemporalDiagnostics d;
            using (var a = new TemporalFixture(W, H, setup))
            {
                a.Frames(Before, Draw);
                a.Scene.Post.Temporal.MipBiasOffset = 1f;
                changed = a.Frame(Draw);
                d = a.Scene.LastTemporalDiagnostics;
            }
            using (var b = new TemporalFixture(W, H, biased))
            {
                b.SkipFrames(Before);
                scratch = b.Frame(Draw);
            }
            using (var b = new TemporalFixture(W, H, setup))
            {
                b.SkipFrames(Before);
                scratchAtDefault = b.Frame(Draw);
            }
            var kept = Compare(changed, scratch);
            var effect = Compare(scratch, scratchAtDefault);
            output.WriteLine($"mip bias offset -0.5 to 1: {Describe(d)}");
            output.WriteLine($"  against from scratch: worst {kept.Worst} steps, mean {kept.Mean:0.0000}");
            output.WriteLine($"  from scratch at 1 against at -0.5: worst {effect.Worst} steps, "
                + $"mean {effect.Mean:0.0000}");

            Assert.True(effect.Mean >= MinKeptHistoryMeanStep,
                $"the offset change must alter the frame, or the case measures nothing (mean {effect.Mean:0.0000})");
            Assert.True(d.HistoryValid, "a mip bias offset change must keep history");
            Assert.Equal(TemporalResetReason.FirstFrame, d.LastReset);
            Assert.True(kept.Mean >= MinKeptHistoryMeanStep,
                $"the frame is too close to a from-scratch render to have kept history (mean {kept.Mean:0.0000})");
        }
    }
}
