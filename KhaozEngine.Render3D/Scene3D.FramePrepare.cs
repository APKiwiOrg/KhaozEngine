using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The scene's pre-record phase reserves cold resources and runs once-only producers before any command list opens.
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
        /// This phase can grow point-light, coverage, outcome and temporal storage or run a producer's own GPU
        /// work. Its first call requires no open recording, even when no water is queued. A nested call refuses
        /// with <see cref="GpuNestedRecordingException"/> before capacity or preparation state changes.
        /// </para>
        /// <para>
        /// The order is <see cref="Begin"/> -> queue the frame's draws -> <c>PrepareFrame</c> -> open the frame's
        /// command list -> render. Preparing for the first time inside the render list is refused by name.
        /// </para>
        /// <para>
        /// <b>Once per frame, and the second call is a no-op</b> rather than a second preparation.
        /// <see cref="Render3DSurface.Render"/> calls it, so a host that also calls it by hand would otherwise
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
        public void PrepareFrame()
        {
            if (_framePrepared) return;
            if (GpuRecording.OpenOwner(_gd) is { } owner)
                throw new GpuNestedRecordingException(owner, "Scene3D.PrepareFrame");
            EnsurePointLightCapacity();
            ReserveCoverageMotion();
            ReserveCoverageGpuFrame();
            _framePrepared = true;
            HarvestTemporalCounts();   // an armed temporal count request: its one readback, here before any recording
            var frame = new FramePrepare(Post.Water, RelativeWaterPlanes(), EffectTimeSeconds);
            IFramePreparer[] preparers = _preparers ??= new IFramePreparer[] { _water };
            for (int i = 0; i < preparers.Length; i++) preparers[i].PrepareFrame(frame);
        }
    }
}
