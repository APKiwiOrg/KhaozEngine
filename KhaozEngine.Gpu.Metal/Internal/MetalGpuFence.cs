using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The seam's <see cref="IGpuFence"/> on the native Metal backend: a REMEMBERED VALUE on the device's one
    /// <see cref="MetalTimeline"/>, and no Metal object of its own.
    /// <para>
    /// THE WHOLE TYPE IS THAT SENTENCE. An <c>MTLSharedEvent</c> gives a device one monotonic counter rather
    /// than a completion callback per submission, so "has submission N finished" is answered by comparing that
    /// counter against the value submission N signalled. Every fence the seam hands out is therefore two fields
    /// and a comparison, and the expensive object is created once per device. There is no
    /// <c>ManualResetEvent</c>, no per-fence dictionary entry and no allocation on the completion path anywhere
    /// in this backend, all three of which the incumbent has.
    /// </para>
    /// <para>
    /// THE LIFECYCLE. A fence is UNARMED when created, which reads unsignalled, because the seam requires a
    /// fence to be unsignalled when it is submitted. The submit path arms it with the value that submission was
    /// allocated, and from then on <see cref="Signaled"/> is that value against the timeline's completed value.
    /// <see cref="Reset"/> unarms it again so it can be handed to a later submit.
    /// </para>
    /// <para>
    /// RESET CANNOT UNSIGNAL ANYTHING, and that is not a compromise. The counter is device-wide and monotonic,
    /// so there is nothing to wind back and nothing that would want to be: a reset fence is re-armed by its next
    /// submission with a strictly HIGHER value than the one it just held, which is exactly the fresh target the
    /// seam asks for. The incumbent's Veldrid fence has real per-object state to clear, and this is the same
    /// contract reached with less.
    /// </para>
    /// <para>
    /// A POLL NEVER WAITS AND NEVER TAKES A LOCK. <see cref="Signaled"/> is one <c>signaledValue</c> property
    /// read, which touches no queue and no command buffer, so the seam's "it polls and returns" is met exactly
    /// rather than nearly. That is the property the whole design leans on: <c>RetiredResourcePool</c> polls
    /// constantly and must not serialise against submission to do it.
    /// </para>
    /// <para>
    /// AFTER DEVICE DEATH IT READS TRUE (M-F6), and that wins over everything else including an unarmed fence. A
    /// dead device has no outstanding work, so "is it done" is yes, and answering no is what would strand a
    /// retire pool on a batch it can never free.
    /// </para>
    /// <para>
    /// DISPOSAL RELEASES NOTHING, because there is nothing here to release. It latches, so a fence used after it
    /// is disposed fails on the path where that is a defect (arming, reached from the submit path) and stays
    /// quiet on the paths where it is a teardown-order accident (polling and resetting). Nothing in the seam's
    /// contract says a fence has to be disposed at all, and a consumer that pools them
    /// (<c>GpuRetireBarrier</c> does) disposes them last, after the device.
    /// </para>
    /// </summary>
    internal sealed class MetalGpuFence : IGpuFence
    {
        readonly MetalTimeline _owner;

        // 0 is "unarmed". The shared event is created at 0 and the first submission takes 1, so no real target
        // can collide with it.
        ulong _target;
        bool _disposed;

        internal MetalGpuFence(MetalTimeline owner)
            => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        /// <summary>The timeline value this fence is waiting on, or 0 when it is unarmed. Diagnostic and test
        /// surface: the seam sees only <see cref="Signaled"/>.</summary>
        internal ulong Target => _target;

        /// <summary>
        /// THE TIMELINE THIS FENCE'S VALUE IS A POINT ON, which the submit path compares by REFERENCE against its
        /// own. Each device has exactly one timeline (M-F1), so timeline identity is device identity, and a fence
        /// from another native Metal device would otherwise be armed with a value on the wrong counter and report
        /// <see cref="Signaled"/> about work this device never ran.
        /// </summary>
        internal MetalTimeline Timeline => _owner;

        /// <inheritdoc/>
        public bool Signaled
        {
            get
            {
                // Death first, and it wins over the unarmed case too (M-F6). See the class note.
                if (_owner.IsDeviceDead) return true;
                if (_target == 0) return false;

                return _owner.CompletedValue >= _target;
            }
        }

        /// <inheritdoc/>
        public void Reset() => _target = 0;

        /// <inheritdoc/>
        public void Dispose() => _disposed = true;

        /// <summary>
        /// Bind this fence to <paramref name="target"/>, the value allocated to the submission it was handed to.
        /// Called by the submit path (row 7, https://github.com/APKiwiOrg/KhaozEngine/issues/573) and by nothing
        /// else.
        /// </summary>
        internal void Arm(ulong target)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (target == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(target),
                    "A native Metal fence was armed with timeline value 0, which is the unarmed marker. The "
                    + "device's shared event is created at 0 and the first submission takes 1, so reaching here "
                    + "means a fence was armed without a value having been allocated.");
            }

            if (_target != 0)
            {
                throw new InvalidOperationException(
                    "A native Metal fence was submitted while it was still armed from an earlier submission. "
                    + "The seam requires a fence to be unsignaled when it is submitted, so call Reset between "
                    + "submissions. Overwriting the target silently instead would make the earlier submission's "
                    + "completion unobservable, and a consumer polling for it would free resources the GPU is "
                    + "still reading.");
            }

            _target = target;
        }
    }
}
