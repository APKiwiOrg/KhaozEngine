using System;

namespace KhaozEngine.Render3D
{
    public sealed partial class FollowCamera3D
    {
        /// <summary>
        /// Optional frame clock: an id that advances once per frame, before the frame's first camera read, and holds
        /// for the rest of the frame. Over a <c>GameApp</c> that is <c>() =&gt; Clock.FrameCount</c>, which the app
        /// ticks before <c>OnUpdate</c>. When set, each computed <see cref="Eye"/> is stamped with the id, a read
        /// reuses the cached eye only under the same id and the same inputs, and <see cref="BeginFrame"/> keeps an eye
        /// already computed this frame instead of dropping it.
        /// <para>
        /// That is the whole point: a consumer that reads the eye in its update, after moving the target, and again
        /// through the render pays ONE computation (and one <see cref="BoomProbe"/> call) a frame instead of two.
        /// The staleness bound holds by the stamp: an eye computed under an earlier id is never answered again, so a
        /// camera whose inputs never change still recomputes once a frame and sees a wall that slid in behind its
        /// probe. See <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1189">#1189</see>.
        /// </para>
        /// <para>
        /// Null (the default) leaves the camera exactly as it was: <see cref="BeginFrame"/> drops the cache every time.
        /// A clock that stops advancing falls back to that same behaviour at every latch after the first, and a clock
        /// that advances mid-frame costs a recompute, never a stale eye. The clock is called on every read, so it must
        /// be cheap and must not allocate. The payoff needs the update's reads to come after its last camera write:
        /// a knob written after an update read still costs the render a recompute, as it always has.
        /// </para>
        /// </summary>
        public Func<long>? FrameClock;

        long _eyeFrame;                     // the FrameClock id the cached eye was computed under
        long _latchFrame = long.MinValue;   // the FrameClock id the previous BeginFrame saw

        /// <summary>
        /// A new frame has started. With no <see cref="FrameClock"/>, drop the cached <see cref="Eye"/>, as
        /// <see cref="IIsoCamera3D.BeginFrame"/> describes. With a clock, keep an eye computed earlier in this same
        /// frame (the stamp already expires one from any earlier frame), unless the clock reads the same id as it did
        /// at the previous latch: then it is not being ticked, this latch is the only frame boundary the camera sees,
        /// and it drops the cache exactly as a camera with no clock does. <see cref="Scene3D"/> calls this on its
        /// active camera. Call it yourself once per frame if you drive this camera without a <see cref="Scene3D"/>.
        /// </summary>
        public void BeginFrame()
        {
            if (FrameClock is not { } clock)
            {
                InvalidateEye();
                return;
            }
            long frame = clock();
            if (frame == _latchFrame) InvalidateEye();
            _latchFrame = frame;
        }

        /// <summary>True with no clock, or when the cached eye was computed under the clock's current id.</summary>
        bool EyeIsFromThisFrame() => FrameClock is not { } clock || clock() == _eyeFrame;

        /// <summary>Stamps a fresh computation with the clock's current id.</summary>
        void StampEyeFrame() => _eyeFrame = FrameClock is { } clock ? clock() : 0L;
    }
}
