using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The frame's pre-recording phase: the half of <see cref="Scene3D"/> that runs the subsystems which have GPU
    /// work of their own to submit, at the one point in the frame where no command list is open.
    /// See <see cref="IFramePreparer"/> for why that point has to exist, and
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see> for what happens when it does not.
    /// </summary>
    public sealed partial class Scene3D
    {
        // Built on first use rather than in the constructor, so this phase costs a scene that never renders
        // nothing at all. One entry today, and the array is the registration, not a placeholder for generality.
        IFramePreparer[]? _preparers;

        // Whether this frame has already been prepared. Cleared by Begin, which is what makes it PER FRAME.
        bool _framePrepared;

        /// <summary>
        /// Run the frame's pre-recording work. Call it ONCE per frame, after every <c>Draw*</c> call for the frame
        /// and BEFORE opening the command list the scene is rendered into.
        /// <para>
        /// Some of what a frame needs cannot be recorded into the frame's list: the FFT ocean's priming pass is a
        /// compute dispatch whose output another dispatch reads in the same frame, and the GPU seam's only ordering
        /// for that pair is a submit plus a device wait (#311), which means a command list of its own. Opening one
        /// while the frame's list is recording is what corrupts the device on Direct3D11 in immediate-context mode
        /// (#423), so it happens here instead. An empty water queue prepares no ocean resources.
        /// </para>
        /// <para>
        /// The order is <see cref="Begin"/> -> queue the frame's draws -> <c>PrepareFrame</c> -> open the frame's
        /// command list -> render. Skipping it throws from the water pass rather than rendering a stale ocean.
        /// </para>
        /// <para>
        /// <b>Once per frame, and the second call is a no-op</b> rather than a second preparation.
        /// <see cref="Render3DSurface.Render"/> checks the no-argument latch, so a host would otherwise
        /// prepare twice, and a second preparation is not harmless: it advances the wave clock again with a zero
        /// delta and hands the frame a surface one frame behind, silently. The flag is cleared by
        /// <see cref="Begin"/>, so it is the FRAME that is prepared once, not the scene. Queueing a draw after
        /// preparing is the misuse this cannot absorb, and the water pass throws for it.
        /// </para>
        /// <para>
        /// The other side of that latch: recording CONSUMES the prepared frame, so a scene records once per
        /// <see cref="Begin"/>. A host that wants the same scene recorded twice (two viewports, a split screen)
        /// begins again and re-queues, rather than rendering a second time off one preparation.
        /// </para>
        /// </summary>
        public void PrepareFrame(int viewportWidth, int viewportHeight)
        {
            if (viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
            if (viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
            if (_framePrepared)
            {
                if (_waterViewportPrepared && HasBoundWater()) VerifyPreparedWaterTargets(viewportWidth, viewportHeight);
                return;
            }
            if (GpuRecording.OpenOwner(_gd) is { } owner)
                throw new GpuNestedRecordingException(owner, "scene viewport preparation");
            _waterViewportPrepared = false;
            EnsureSize(viewportWidth, viewportHeight);
            _res.EnsureDistortion(_distortionSprites.Count > 0, DistortionQuality == DistortionQuality.Full ? 2 : 4);
            PrepareSubsystems(optics: true);
            _preparedWaterTargets = WaterTargetInputs(viewportWidth, viewportHeight);
            _preparedWaterTargetGeneration = _res.Generation;
            _waterViewportPrepared = true;
            _framePrepared = true;
        }

        /// <summary>Prepare legacy scene-depth water and other frame producers. Bound water requires the
        /// viewport overload in the host's pre-record callback, so targets are final before sets are created.</summary>
        public void PrepareFrame()
        {
            if (_framePrepared) return;
            if (HasBoundWater()) throw MissingWaterViewport();
            _waterViewportPrepared = false;
            PrepareSubsystems(optics: false);
            _framePrepared = true;
        }

        // Optical bindings resolve inside the same preparer phase, after the legacy preparers they depend on.
        // The no-argument form prepares the column colour source at render instead, after final sizing.
        void PrepareSubsystems(bool optics)
        {
            using var phase = ProfileCpu("cpu.water.prepare");
            EnsurePointLightCapacity();
            ValidateWaterColumnInputs();
            BindWaterColumnShadows(_water);
            HarvestTemporalCounts();   // an armed temporal count request: its one readback, here before any recording
            var frame = new FramePrepare(Post.Water, WaterDemandPlanes(), EffectTimeSeconds);
            IFramePreparer[] preparers = _preparers ??= new IFramePreparer[] { _water };
            for (int i = 0; i < preparers.Length; i++) preparers[i].PrepareFrame(frame);
            if (optics) PrepareWaterOptics(new FramePrepare(Post.Water, RelativeWaterPlanes(), EffectTimeSeconds));
        }

        // The frame's one device.water.prepare root owns the column colour source and the optics sets that bind it,
        // so the source is ready before any set is created. A CPU phase id opens at most once per frame.
        void PrepareWaterOptics(in FramePrepare frame)
        {
            using var device = _water.Profiling?.Cpu("device.water.prepare") ?? default;
            PrepareWaterColumnColor();
            _water.PrepareOpticsFrameCore(_res, frame, CollectionsMarshal.AsSpan(_waterPlanes), _boundedWater.Count);
        }

        bool _waterViewportPrepared;
        int _preparedWaterTargetGeneration;
        PreparedWaterTargets _preparedWaterTargets;

        readonly record struct PreparedWaterTargets(int ViewportWidth, int ViewportHeight,
            int Width, int Height, int Samples, bool Mips, bool Bloom, bool Hdr, bool Motion,
            bool Visibility, bool Pings, bool Distortion, DistortionQuality DistortionQuality, bool Resolve);

        PreparedWaterTargets WaterTargetInputs(int width, int height)
        {
            var (w, h) = ComputeTargetSize(Post, width, height);
            bool motion = TemporalActive;
            return new(width, height, w, h, ResolvedMsaaSamples(), WantsMipDownsample(Post, width, height),
                InternalBloomWanted, Post.Hdr.Enabled, motion,
                ShadowVisibilityTargets.Requested(motion || _requestLitVisibility, _gd), InternalPingsWanted,
                _distortionSprites.Count > 0, DistortionQuality, TemporalResolveActive);
        }

        bool HasBoundWater()
        {
            for (int i = 0; i < _waterPlanes.Count; i++)
                if (_waterPlanes[i].Optics.IsBound) return true;
            return false;
        }

        static InvalidOperationException MissingWaterViewport() => new(
            "Bound water requires unchanged viewport and target inputs prepared with Scene3D.PrepareFrame(width, height) before recording.");

        void VerifyPreparedWaterTargets(int width, int height)
        {
            if (!_framePrepared || !_waterViewportPrepared || _preparedWaterTargets != WaterTargetInputs(width, height)
                || _preparedWaterTargetGeneration != _res.Generation) throw MissingWaterViewport();
            if (_waterPlanes.Count > 0)
                _water.VerifyOpticsFrame(_res, RelativeWaterPlanes(), Post.Water, EffectTimeSeconds);
        }

        // Called before the recording path can resize or replace anything. Only bound water freezes its targets at
        // preparation. A frame with no bound water keeps the legacy late binding on either preparation form, so its
        // targets may still resize here and the water pass rebinds them.
        void EnsurePreparedRenderSize(int width, int height)
        {
            if (HasBoundWater()) VerifyPreparedWaterTargets(width, height);
            EnsureSize(width, height);
            ValidateWaterColumnInputs();
            PrepareWaterColumnColor();
        }
    }
}
