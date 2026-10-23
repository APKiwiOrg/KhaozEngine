using System;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <c>IGpuDevice.TryReadBuffer</c> ON THE NATIVE DIRECT3D 11 BACKEND: a read of a staging buffer whose
    /// recorded copy has completed, that never waits for anything. Device-free, so every refusal and both "not
    /// yet" answers are driven by a plain <c>[Fact]</c> through <see cref="ID3D11StagingMemory"/> and
    /// <see cref="ID3D11FenceTimeline"/> fakes. The device holds one and forwards to it.
    /// <para>
    /// THE ORDER IS THE CONTRACT. Every refusal comes first, ahead of any lock, poll or native call: a dead
    /// device (checked before the fence, because a dead device's fence reads signalled), the fence's owner, and
    /// the buffer's owner, usage and window. Then the immediate-context lock is TRIED with
    /// <see cref="Monitor.TryEnter(object)"/>, so a replay or a synchronous map on another thread answers false
    /// rather than blocking. The fence is polled inside that lock, which is what lets the event-query fallback's
    /// own poll (it takes the same lock, re-entrantly) cost nothing extra, and then
    /// <see cref="D3D11StagingAccess.TryReadBuffer"/> maps with <c>DO_NOT_WAIT</c>, copies and unmaps before
    /// the lock is released.
    /// </para>
    /// <para>
    /// NOTHING HERE DRAINS, FLUSHES OR SIGNALS. A fence that is not yet signalled is answered false without a
    /// flush. <c>Submit</c> does not flush the immediate context either, so the submission reaches the driver at
    /// the next <c>Present</c>, drain or driver-initiated flush, which in a presenting frame loop is the frame
    /// boundary. A headless loop that neither presents nor drains can therefore read false indefinitely, and that
    /// is the documented cost of never waiting. The fence is the caller's and this type never resets it: a reset
    /// or reused fence is a caller error, not a new completion.
    /// </para>
    /// </summary>
    internal sealed class D3D11CompletedReads
    {
        readonly D3D11StagingAccess _staging;
        readonly D3D11FenceSubsystem _fences;
        readonly object _submitLock;
        readonly IDeviceLiveness _liveness;

        /// <param name="staging">The device's staging map path.</param>
        /// <param name="fences">The device's fence subsystem, which is the identity a fence must carry.</param>
        /// <param name="submitLock">The device's single submit lock (decision W4).</param>
        /// <param name="liveness">The device's liveness token, which is the identity a buffer must carry.</param>
        internal D3D11CompletedReads(D3D11StagingAccess staging, D3D11FenceSubsystem fences, object submitLock,
            IDeviceLiveness liveness)
        {
            _staging = staging ?? throw new ArgumentNullException(nameof(staging));
            _fences = fences ?? throw new ArgumentNullException(nameof(fences));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        }

        /// <summary>See <c>IGpuDevice.TryReadBuffer</c>, and the type remarks for the order.</summary>
        internal bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes,
            Span<byte> destination)
        {
            if (_liveness.IsDead)
            {
                throw new ObjectDisposedException(nameof(IGpuDevice),
                    "The native Direct3D 11 device has been disposed or lost, so nothing on it can be read. Its "
                    + "fences read signalled after death, which is why this is refused before one is polled.");
            }

            ArgumentNullException.ThrowIfNull(staging);
            ArgumentNullException.ThrowIfNull(completion);

            if (completion is not D3D11GpuFence fence)
            {
                throw new ArgumentException(
                    $"A {completion.GetType().Name} was handed to the native Direct3D 11 backend as the completion "
                    + "fence of a staging copy. Only a fence this backend created names a value on its timeline.",
                    nameof(completion));
            }

            fence.RequireOwner(_fences, nameof(completion));

            ID3D11ReadableStaging buffer = D3D11StagingAccess.RequireReadable(staging, offsetBytes,
                destination.Length);
            if (!ReferenceEquals(buffer.Owner, _liveness))
            {
                throw new ArgumentException(
                    "That staging buffer was created by a DIFFERENT native Direct3D 11 device, so this device's "
                    + "immediate context cannot map it. Read it through the device that created it.",
                    nameof(staging));
            }

            if (!Monitor.TryEnter(_submitLock)) return false;
            try
            {
                if (!fence.Signaled) return false;

                return _staging.TryReadBuffer(staging, offsetBytes, destination);
            }
            finally
            {
                Monitor.Exit(_submitLock);
            }
        }
    }
}
