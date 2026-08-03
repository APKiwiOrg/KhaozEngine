using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The seam's <see cref="IGpuFence"/> on the native Direct3D 11 backend: a REMEMBERED VALUE on the device's
    /// one <see cref="ID3D11FenceTimeline"/>, and no device object of its own.
    /// <para>
    /// THE WHOLE TYPE IS THAT SENTENCE. Direct3D 11.4 gives a device one monotonic counter rather than a fence
    /// per submission, so "has submission N finished" is answered by comparing that counter against the value
    /// submission N signalled. Every fence the seam hands out is therefore two fields and a comparison, and the
    /// expensive object is created once per device.
    /// </para>
    /// <para>
    /// THE LIFECYCLE, which is the part a reader has to get right. A fence is UNARMED when created, which reads
    /// unsignalled, because the seam requires a fence to be unsignalled when it is submitted. <c>Submit</c> arms
    /// it with the value that submission's end-of-replay signal produced, and from then on
    /// <see cref="Signaled"/> is that value against the timeline's completed value.
    /// <see cref="Reset"/> unarms it again so it can be submitted a second time.
    /// </para>
    /// <para>
    /// RESET CANNOT UNSIGNAL ANYTHING, and that is not a compromise. The counter is device-wide and monotonic, so
    /// there is nothing to wind back and nothing that would want to be: a reset fence is re-armed by its next
    /// submission with a strictly HIGHER value than the one it just held, which is exactly the fresh target the
    /// seam asks for. Veldrid's fence has real per-object state to clear, and this one is the same contract
    /// reached with less.
    /// </para>
    /// <para>
    /// A POLL DOES NOT WAIT, on the mechanism nearly every machine gets. <see cref="Signaled"/> reads the
    /// device-wide counter, and on the monotonic fence that read takes no lock at all, because
    /// <c>GetCompletedValue</c> is free-threaded. On the event-query fallback the same read DOES take the
    /// device's submit lock, since that mechanism polls the immediate context, so there a poll from a thread that
    /// is not the submitting one can wait as long as a whole replay. The reasoning behind keeping that difference
    /// rather than levelling it is on <see cref="D3D11FenceSubsystem"/>.
    /// </para>
    /// <para>
    /// DISPOSAL RELEASES NOTHING, because there is nothing here to release. It latches, so a fence used after it
    /// is disposed fails on the path where that is a defect (arming, reached from <c>Submit</c>) and stays quiet
    /// on the paths where it is a teardown-order accident (polling and resetting). Nothing in the seam's contract
    /// says a fence has to be disposed at all, and a consumer that pools them (<c>GpuRetireBarrier</c> does)
    /// disposes them last, after the device.
    /// </para>
    /// </summary>
    internal sealed class D3D11GpuFence : IGpuFence
    {
        readonly D3D11FenceSubsystem _owner;

        // 0 is "unarmed". The timeline's first signal is 1, so no real target can collide with it.
        ulong _target;
        bool _disposed;

        internal D3D11GpuFence(D3D11FenceSubsystem owner)
            => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        /// <summary>The timeline value this fence is waiting on, or 0 when it is unarmed. Diagnostic and test
        /// surface: the seam sees only <see cref="Signaled"/>.</summary>
        internal ulong Target => _target;

        /// <inheritdoc/>
        public bool Signaled
        {
            get
            {
                // Death first, and it wins over everything else including an unarmed fence (decision X3). A
                // destroyed device has no outstanding work, so "is it done" is yes, and answering no here is what
                // would strand a retire pool on a batch it can never free.
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
        /// Bind this fence to <paramref name="target"/>, the value the submission it was handed to signalled.
        /// Called by <see cref="D3D11FenceSubsystem"/> at the end of that submission's replay and by nothing
        /// else.
        /// </summary>
        internal void Arm(ulong target)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (target == 0)
                throw new ArgumentOutOfRangeException(nameof(target),
                    "A Direct3D 11 fence was armed with timeline value 0, which is the unarmed marker. The "
                    + "timeline's first signal is 1, so reaching here means a fence was armed without a signal.");

            if (_target != 0)
                throw new InvalidOperationException(
                    "A Direct3D 11 fence was submitted while it was still armed from an earlier submission. The "
                    + "seam requires a fence to be unsignaled when it is submitted, so call Reset between "
                    + "submissions. Overwriting the target silently instead would make the earlier submission's "
                    + "completion unobservable, and a consumer polling for it would free resources the GPU is "
                    + "still reading.");

            _target = target;
        }
    }
}
