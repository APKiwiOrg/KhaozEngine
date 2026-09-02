using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHAT A HALF-BUILT DEVICE RELEASES WHEN A LATER CONSTRUCTION STEP THROWS
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/503), and the reason it is a type rather than a catch
    /// block full of null checks.
    /// <para>
    /// <see cref="D3D11GpuDevice"/> builds nine subsystems in dependency order and five of them own a COM object.
    /// Every one of those objects holds a reference count on the <c>ID3D11Device</c>, so a throw partway through
    /// used to leave the caller dropping ONE reference on a device several orphans still held: the native device
    /// and every driver allocation behind it then lived until the process exited. The reachable trigger is the
    /// swapchain, which DXGI can refuse for a window handle or a display in a bad state, on a path the device
    /// context catches and falls back from, so the symptom is a session that fell back with a fully allocated
    /// orphan device sitting beside it.
    /// </para>
    /// <para>
    /// IT IS DEVICE-FREE ON PURPOSE, which is the whole reason the fix is shaped this way. The constructor it
    /// serves is Windows-only end to end and no test on any other machine can execute a line of it, but the RULE
    /// that matters (everything already built is released, newest first, and nothing built later is touched) is
    /// ordinary bookkeeping over <see cref="Action"/>s. Driven with fakes that throw from a chosen step, that
    /// rule is a plain <c>[Fact]</c> on every leg.
    /// </para>
    /// <para>
    /// UNWIND NEVER THROWS. A release that fails during unwind would replace the construction exception the
    /// caller needs to see with one about the cleanup, so each failure goes to the constructor's
    /// <c>onReleaseFailure</c> callback and the walk carries on to the releases after it. That callback is the
    /// device's logger in production and a recorder in a test.
    /// </para>
    /// </summary>
    internal sealed class D3D11ConstructionScope
    {
        readonly List<Action> _releases = new();
        readonly Action<Exception>? _onReleaseFailure;

        bool _committed;
        bool _unwound;

        /// <param name="onReleaseFailure">Told about each release that threw during <see cref="Unwind"/>, or null
        /// to swallow them. Never called on the success path.</param>
        internal D3D11ConstructionScope(Action<Exception>? onReleaseFailure = null)
            => _onReleaseFailure = onReleaseFailure;

        /// <summary>How many releases are registered. For a test, and for nothing else.</summary>
        internal int TrackedCount => _releases.Count;

        /// <summary>Whether construction reached <see cref="Commit"/>, after which <see cref="Unwind"/> does
        /// nothing at all.</summary>
        internal bool IsCommitted => _committed;

        /// <summary>
        /// Register a subsystem that has just been built, and hand it straight back so the call reads as part of
        /// the assignment it belongs to (<c>_fences = scope.Track(new ...)</c>).
        /// </summary>
        internal T Track<T>(T built) where T : class, IDisposable
        {
            ArgumentNullException.ThrowIfNull(built);

            _releases.Add(built.Dispose);
            return built;
        }

        /// <summary>
        /// Register a release that is not the object's own <c>Dispose</c>. The device's shared sampler pair is
        /// exactly that case: it is deliberately NON-owning, so its <c>Dispose</c> is a no-op a consumer cannot
        /// hurt anything with, and only the device's own destroy actually frees the sampler state.
        /// </summary>
        internal void TrackRelease(Action release)
        {
            ArgumentNullException.ThrowIfNull(release);

            _releases.Add(release);
        }

        /// <summary>Construction finished. From here <see cref="Unwind"/> is a no-op, because the object now owns
        /// everything registered and releases it at teardown instead.</summary>
        internal void Commit() => _committed = true;

        /// <summary>
        /// Release everything registered, NEWEST FIRST, unless construction committed. Idempotent, and it never
        /// throws: see the type note.
        /// </summary>
        internal void Unwind()
        {
            if (_committed || _unwound) return;
            _unwound = true;

            for (int i = _releases.Count - 1; i >= 0; i--)
            {
                try
                {
                    _releases[i]();
                }
                catch (Exception ex)
                {
                    _onReleaseFailure?.Invoke(ex);
                }
            }

            _releases.Clear();
        }
    }
}
