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
        /// <para>
        /// THE END-OF-REPLAY SIGNAL RIDES HERE (decision C5), which is what makes one submit exactly one point
        /// the timeline can name. It is raised after <see cref="Replay{TEmitter}"/> and inside the lock, so it
        /// lands after the last command of this submission on BOTH drivers: the deferred one has just emitted the
        /// whole stream, and the immediate one emitted during record and has nothing left to emit. Placing it
        /// before the replay instead would name a point the GPU reaches before the submission is finished, and a
        /// fence polled there reports work complete that has not been issued.
        /// </para>
        /// <para>
        /// BOTH TRAILING ARGUMENTS ARE OPTIONAL, and a submit that names neither replays exactly as it always
        /// has and signals nothing. That is every device-free driver test and the whole package as it stands,
        /// since no shipped path constructs a device yet. The device row passes its
        /// <see cref="D3D11FenceSubsystem"/> as <paramref name="signal"/> and whatever fence the seam's
        /// <c>Submit(IGpuCommandList, IGpuFence)</c> was handed as <paramref name="fence"/>, and a fenceless
        /// submit still signals because the timeline has to advance with the submission stream for a later
        /// fence's value to cover the earlier work at all.
        /// </para>
        /// <para>
        /// A REJECTED SUBMIT SIGNALS NOTHING, because <see cref="Replay{TEmitter}"/> throws before the signal is
        /// reached. That is the right direction rather than an accident of ordering: an unsealed or foreign list
        /// emitted no commands, so there is no point on the timeline for it to name.
        /// </para>
        /// <para>
        /// THE OPPOSITE DIRECTION IS NOT SAFE THE SAME WAY. If the signal itself throws, the exception escapes
        /// this method AFTER replay has already emitted the submission's commands to the device, and that
        /// emission cannot be undone. <see cref="D3D11FenceSubsystem.SignalEndOfReplay"/> can throw
        /// <see cref="ArgumentException"/> for a foreign fence, <see cref="InvalidOperationException"/> for a
        /// still-armed one, or <see cref="ObjectDisposedException"/> for a disposed one, and a throwing signal
        /// never means the submission was rejected. It means the commands are already out, and the caller must
        /// treat the submission as issued regardless of the exception. What each of those three paths leaves
        /// behind on the timeline and on the fence is documented on the subsystem, not repeated here.
        /// </para>
        /// <para>
        /// THE RING BRACKETS THE REPLAY (decisions U2 and U5), which is why <paramref name="rings"/> is here and
        /// not on the device row alone. Every mapped constant-buffer ring is unmapped BEFORE the replay, because
        /// Direct3D 11 forbids a mapped resource being bound to the pipeline and the replay is about to bind them,
        /// and the value the submission signalled is recorded against the current segment AFTER it, because that
        /// is the value the next owner of that segment has to wait for. The unmap is the reason "zero Map or
        /// Unmap during replay" is a structural invariant rather than a hope.
        /// </para>
        /// <para>
        /// THE WHOLE BRACKET IS ONE CRITICAL SECTION, unmap included. The submit lock is taken once and held from
        /// the unmap through the replay to the signal, because an off-timeline
        /// <see cref="D3D11RingAllocator.UpdateBuffer"/> landing between an unmap that released the lock and a
        /// replay that then took it would map the ring again and the replay would bind mapped memory. The
        /// allocator takes the same lock inside its own unmap, which is a free re-entry on the thread that
        /// already owns it.
        /// </para>
        /// <para>
        /// A RING ALLOCATOR WITHOUT A SIGNAL IS REFUSED, for the same shape of reason as a fence without one and
        /// with a worse failure behind it. The segment would carry no completion value, so it would be handed back
        /// out with no wait, and the CPU would write uniforms into memory the GPU is still reading. That is a
        /// corrupted frame rather than a hang, it is intermittent, and it looks like a rendering bug several
        /// frames from its cause.
        /// </para>
        /// </summary>
        internal static void Submit<TEmitter>(object submitLock, IGpuCommandList list, ref TEmitter emitter,
            ID3D11SubmitSignal? signal = null, IGpuFence? fence = null, D3D11RingAllocator? rings = null)
            where TEmitter : struct, ID3D11Emitter
        {
            if (submitLock is null) throw new ArgumentNullException(nameof(submitLock));

            // A fence with nothing to arm it is the one combination of the two that is always a defect, and it is
            // a defect that goes quiet: the fence stays unarmed, so it reads unsignalled forever and whatever is
            // waiting on it waits forever. That is a hang rather than a wrong pixel, and it surfaces in the
            // retire pool rather than here, so it is refused at the call that made it.
            if (signal is null && fence is not null)
            {
                throw new ArgumentException(
                    "A fence was handed to a Direct3D 11 submit that has no signal sink, so nothing would ever "
                    + "arm it. An unarmed fence never reads signalled, and a consumer polling for that "
                    + "submission's completion waits forever. Pass the device's fence subsystem alongside the "
                    + "fence.", nameof(fence));
            }

            if (signal is null && rings is not null)
            {
                throw new ArgumentException(
                    "A constant-buffer ring allocator was handed to a Direct3D 11 submit that has no signal sink, "
                    + "so the segment this submission used would carry no completion value. The next frame to "
                    + "reach that segment would take it with no wait and overwrite uniforms the GPU is still "
                    + "reading. Pass the device's fence subsystem alongside the rings.", nameof(rings));
            }

            lock (submitLock)
            {
                // INSIDE the lock, and that placement is the correctness of it rather than tidiness. An unmap
                // taken outside leaves a gap between its own lock release and this one's acquisition, and a
                // device-level UpdateBuffer arriving on any thread in that gap maps the ring again (it maps
                // idempotently, and under AcrossRecording it leaves the mapping in place for the next record
                // phase), so the replay below would bind a mapped constant buffer, which Direct3D 11 forbids.
                // Holding the lock across the unmap costs the unmap's own duration and nothing else, since the
                // lock is held across the whole replay either way. It is a no-op when the submission wrote no
                // uniforms, and it takes the same lock again internally, which is free on a Monitor.
                rings?.UnmapMappedRings();

                Replay(list, ref emitter);
                ulong completionValue = signal?.SignalEndOfReplay(fence) ?? 0UL;
                rings?.OnSubmitted(completionValue);
            }
        }

        /// <summary>
        /// Replay <paramref name="list"/> into <paramref name="emitter"/>, assuming the caller already holds the
        /// submit lock. Separate from <see cref="Submit{TEmitter}"/> so a device that already took the lock for a
        /// present or a resize apply does not take it twice.
        /// <para>
        /// THAT CALLER OWES THE END-OF-REPLAY SIGNAL ITSELF, right after this returns and before it releases the
        /// lock, because the signal belongs to the submission rather than to the replay: this method is also the
        /// inside of <see cref="Submit{TEmitter}"/>, so signalling here would signal twice for every ordinary
        /// submit.
        /// </para>
        /// <para>
        /// IT OWES THE RING BRACKET TOO, for the same reason and in the same order:
        /// <c>UnmapMappedRings</c> before this call and <c>OnSubmitted</c> with the signalled value after it.
        /// Skipping the first binds a mapped resource, and skipping the second leaves the segment ungated. Both
        /// belong INSIDE the lock that caller already holds, and it must not release the lock between the unmap
        /// and this call: an off-timeline write reaching the ring in that window maps it again and the replay
        /// binds mapped memory.
        /// </para>
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
