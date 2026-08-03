using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// Creates command lists on the selected driver and submits them. The one place that knows both drivers
    /// exist, so nothing else in the backend has to branch on <c>KE_D3D11_RECORD</c>: a resource factory calls
    /// <see cref="Create{TEmitter}"/> and a device calls <see cref="Submit{TEmitter}"/>, and neither cares which
    /// driver came back.
    /// <para>
    /// The two drivers differ in exactly one place, which is WHEN the emitter is called.
    /// <see cref="D3D11RecordMode.Deferred"/> hands the recorder a <see cref="D3D11StreamEmitter"/>, so recording
    /// fills a stream and <see cref="Submit{TEmitter}"/> replays it into the real emitter under the submit lock.
    /// <see cref="D3D11RecordMode.Immediate"/> hands the recorder the real emitter directly, so the calls have
    /// already happened by the time submit is reached and there is nothing left to replay.
    /// </para>
    /// </summary>
    internal static class D3D11CommandDrivers
    {
        /// <summary>
        /// Create a command list on <paramref name="mode"/>'s driver. <paramref name="emitter"/> is the device's
        /// real emitter and is used only by the immediate driver, since the deferred one does not meet an
        /// emitter until submit.
        /// <para>
        /// The immediate driver COPIES it into the list, one copy per list. That is safe because an
        /// <see cref="ID3D11Emitter"/> implementation is a readonly struct holding a class reference, so every
        /// list's copy addresses the device's one emitter state, which is where the redundancy caches of R6 and
        /// the scrub of R8 have to live. An emitter with inline mutable state would give each list its own copy
        /// of a cache that describes the shared device context. See the seam for the full reasoning.
        /// </para>
        /// </summary>
        internal static IGpuCommandList Create<TEmitter>(D3D11RecordMode mode, TEmitter emitter)
            where TEmitter : struct, ID3D11Emitter
            => mode == D3D11RecordMode.Immediate
                ? new D3D11CommandRecorder<TEmitter>(emitter)
                : CreateDeferred();

        /// <summary>Create a command list on the deferred driver explicitly.</summary>
        internal static D3D11CommandRecorder<D3D11StreamEmitter> CreateDeferred()
            => new(new D3D11StreamEmitter(new D3D11CommandStream()));

        /// <summary>
        /// Section 5.1's submit, verbatim: take the submit lock, replay, release. The lock is a PARAMETER because
        /// it belongs to the device, where decision W4 puts it, covering replay, present and the resize apply
        /// together. Recording never touches it, which is what lets N lists record while one is submitting.
        /// </summary>
        internal static void Submit<TEmitter>(object submitLock, IGpuCommandList list, ref TEmitter emitter)
            where TEmitter : struct, ID3D11Emitter
        {
            if (submitLock is null) throw new ArgumentNullException(nameof(submitLock));

            lock (submitLock) Replay(list, ref emitter);
        }

        /// <summary>
        /// Replay <paramref name="list"/> into <paramref name="emitter"/>, assuming the caller already holds the
        /// submit lock. Separate from <see cref="Submit{TEmitter}"/> so a device that already took the lock for a
        /// present or a resize apply does not take it twice.
        /// <para>
        /// On the immediate driver this is a no-op with one check, because the native calls were made during
        /// record. That asymmetry is the whole difference between the drivers and it is confined to this method.
        /// </para>
        /// </summary>
        internal static void Replay<TEmitter>(IGpuCommandList list, ref TEmitter emitter)
            where TEmitter : struct, ID3D11Emitter
        {
            switch (list)
            {
                case D3D11CommandRecorder<D3D11StreamEmitter> deferred:
                    RequireSealed(deferred.IsSealed);
                    deferred.Emitter.Stream.Replay(ref emitter);
                    return;

                case D3D11CommandRecorder<TEmitter> immediate:
                    RequireSealed(immediate.IsSealed);
                    return;

                case null:
                    throw new ArgumentNullException(nameof(list));

                default:
                    throw new ArgumentException(
                        $"A {list.GetType().Name} was submitted to the native Direct3D 11 device. Only a command "
                        + "list this backend created can be replayed, because a list from another backend holds "
                        + "another backend's recording.", nameof(list));
            }
        }

        static void RequireSealed(bool isSealed)
        {
            if (!isSealed)
                throw new InvalidOperationException(
                    "A Direct3D 11 command list was submitted without End. End seals the recording, and "
                    + "replaying an unsealed one replays a half-recorded frame instead of failing, which reads "
                    + "as a rendering defect somewhere else entirely.");
        }
    }
}
