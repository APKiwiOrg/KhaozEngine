using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DEFERRED DISPOSAL (V-F9): a native destroy held back until the device timeline has passed the value of the
    /// last submission that could still be reading the thing being destroyed.
    ///
    /// <para><b>WHAT IT CONVERTS, AND FROM WHAT.</b> "Mid-life resource disposal racing queued async work" is one
    /// of the four defects the cross-platform GPU workflow header records as fixed engine-side, and it was fixed
    /// by CONVENTION: callers drain before they dispose, and every caller that forgets is a use-after-free the GPU
    /// finds. Recording a timeline value at <c>Dispose</c> and destroying only once the counter passes it makes
    /// the same guarantee STRUCTURAL. The engine's own <c>WaitForIdle</c> calls stay exactly where they are,
    /// because they are the seam's contract and the Veldrid leg still needs them.</para>
    ///
    /// <para><b>A VALUE AND A CALLBACK, because there is nothing else to be generic over yet.</b> No resource type
    /// exists on this backend: buffers, textures and samplers arrived with row 9
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) and command pools with row 7
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517), and row 7 is why this list is here now rather than
    /// with the first resource. A list over <c>Action</c> is what lets both rows hand it their own destroy without
    /// this type learning either one, and it is why row 7's command lists need no refcount of their own.</para>
    ///
    /// <para><b>OUT-OF-ORDER VALUES ARE ORDINARY, so a drain scans the WHOLE list.</b> Values are allocated by the
    /// submit path and resources are retired by whoever disposes them, so a resource retired later can easily
    /// carry a lower value than one retired earlier. A drain that stopped at the first entry whose value had not
    /// passed would strand every entry behind it, which on a long run is a leak that looks like a memory
    /// regression rather than like a bug in a drain.</para>
    ///
    /// <para><b>CALLBACKS RUN OUTSIDE THE LOCK.</b> The ready entries are taken off the list under the lock and
    /// invoked after it is released, so a destroy that retires something else (a compound resource freeing its
    /// parts) appends to a list nobody is iterating.</para>
    ///
    /// <para><b>A THROWING CALLBACK IS LOGGED AND THE DRAIN CONTINUES.</b> The teardown drain runs between
    /// <c>vkDeviceWaitIdle</c> and <c>vkDestroyDevice</c>, so a callback that threw its way out of the loop would
    /// take the device destroy with it and leak the whole device plus every driver allocation behind it. One
    /// failed destroy is a leak of one object, which is the smaller of the two.</para>
    /// </summary>
    internal sealed class VulkanRetireList
    {
        static readonly ILogger log = Log.For<VulkanRetireList>();

        readonly object _gate = new();
        readonly List<Entry> _entries = new();
        readonly ILogger _log;

        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal VulkanRetireList(ILogger? logger = null) => _log = logger ?? log;

        /// <summary>How many destroys are still held back. A number that climbs and never settles on a running
        /// device means frames stopped or nothing is draining, which is the reading MV6's allocation counts are
        /// taken against.</summary>
        internal int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>
        /// Hold <paramref name="destroy"/> back until the timeline passes <paramref name="value"/>.
        /// <para>
        /// <paramref name="value"/> is the device's CURRENT timeline value at the moment of disposal, which is the
        /// last value allocated to a submission. It is the right value because every submission that could have
        /// referenced this resource has already been made, so a counter that has reached it has finished all of
        /// them. A value of 0 means nothing has ever been submitted, and such an entry is released by the very
        /// next drain, which is correct rather than a special case: a resource nothing has ever referenced is safe
        /// to destroy immediately.
        /// </para>
        /// </summary>
        /// <param name="value">The timeline value that must be passed before the destroy runs.</param>
        /// <param name="destroy">The native destroy. Called at most once, on whichever thread drains.</param>
        internal void Retire(ulong value, Action destroy)
        {
            ArgumentNullException.ThrowIfNull(destroy);

            lock (_gate) _entries.Add(new Entry(value, destroy));
        }

        /// <summary>
        /// Run every held destroy whose value the counter has PASSED, and leave the rest. The frame-boundary hook,
        /// and the ordinary way this list empties.
        /// <para>
        /// IDEMPOTENT AND SAFE TO CALL AT ANY CADENCE. A second drain at the same completed value finds nothing
        /// left to do, because everything ready was removed by the first one, and a drain on an empty list touches
        /// no lock-protected state beyond one length read.
        /// </para>
        /// </summary>
        /// <param name="completedValue">The timeline's completed value, from
        /// <see cref="VulkanTimeline.CompletedValue"/>.</param>
        /// <returns>How many destroys ran.</returns>
        internal int Drain(ulong completedValue) => Release(e => e.Value <= completedValue);

        /// <summary>
        /// Run EVERY held destroy regardless of its value. The teardown drain, and legal in exactly one place: the
        /// device's own <c>Dispose</c>, AFTER its <c>vkDeviceWaitIdle</c> has returned. At that point the GPU is
        /// idle by definition, so every recorded value has been passed and the values have nothing left to say.
        /// </summary>
        /// <returns>How many destroys ran.</returns>
        internal int DrainAll() => Release(static _ => true);

        /// <summary>
        /// Drop every held destroy WITHOUT running it, and report how many were dropped. For the one case where
        /// running them would be the bug: the device is already DEAD, either destroyed or lost, so
        /// <c>vkDestroyDevice</c> (or the driver) already destroyed every object made from it and a destroy call
        /// now is a call against freed memory, which on the Vulkan path aborts the process through the loader
        /// rather than failing quietly.
        /// </summary>
        /// <returns>How many destroys were dropped.</returns>
        internal int Abandon()
        {
            lock (_gate)
            {
                int dropped = _entries.Count;
                _entries.Clear();
                return dropped;
            }
        }

        // The shared body of both drains: take the ready entries off the list under the lock, then invoke them
        // with the lock released. Order is preserved, so destroys run in the order they were retired, which is the
        // order a reader of a native call log expects.
        int Release(Func<Entry, bool> ready)
        {
            List<Entry>? due = null;

            lock (_gate)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    if (!ready(_entries[i])) continue;

                    (due ??= new List<Entry>()).Add(_entries[i]);
                    _entries.RemoveAt(i);
                }
            }

            if (due is null) return 0;

            // Reversed, because the scan above walks backwards so that removal cannot disturb the indices it has
            // yet to visit. The destroys themselves go out in retire order.
            for (int i = due.Count - 1; i >= 0; i--) Invoke(due[i]);
            return due.Count;
        }

        void Invoke(in Entry entry)
        {
            try
            {
                entry.Destroy();
            }
            catch (Exception ex)
            {
                _log.Warn("A deferred native Vulkan destroy threw and was swallowed, so the object it owned is "
                    + $"leaked for the rest of this device's life. It threw {ex.GetType().Name}: {ex.Message}. The "
                    + "drain carries on deliberately: at teardown it runs between vkDeviceWaitIdle and "
                    + "vkDestroyDevice, so letting this out would leak the whole device instead of one object.");
            }
        }

        readonly struct Entry
        {
            internal Entry(ulong value, Action destroy)
            {
                Value = value;
                Destroy = destroy;
            }

            internal ulong Value { get; }

            internal Action Destroy { get; }
        }
    }
}
