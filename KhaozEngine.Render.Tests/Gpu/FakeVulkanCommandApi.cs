using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE COMMAND SEAM WITH NO DRIVER BEHIND IT, recording every call as an EVENT TAGGED WITH THE POOL it
    /// belongs to, so a test can assert what a recording and a submission actually asked the driver for, in order,
    /// per pool.
    /// <para>
    /// This is what makes the whole of row 7 device-free: the slot advance and its wrap, the pool-reset
    /// discipline, the one-time-submit begin, the disposal handover, and above all the SUBMIT ORDERING (which
    /// timeline value was allocated where, which submit carried it, and what happened to it when the submit
    /// failed). All of it runs under a plain <c>[Fact]</c> on a machine with no Vulkan loader.
    /// </para>
    /// <para>
    /// EVERY MUTATION IS UNDER ONE LOCK, unlike the seam it stands in for. The real driver requires external
    /// synchronisation PER POOL and is free-threaded across pools, which is exactly the property the recording
    /// contract test drives N lists concurrently to exercise. A fake with an unsynchronised list behind it would
    /// corrupt its own log under that test and report the corruption as a backend defect, so the log is
    /// serialised and the CONTENT is what the test reads. Nothing here models driver-side contention, and nothing
    /// should: the assertion is about which calls each pool saw and in what order.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that a value was signalled because real GPU work finished, or that a driver
    /// accepted the structures. Those belong to the <c>vulkan-native</c> CI leg (row 19,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/529). The boundary is deliberate, not an omission.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanCommandApi : IVulkanCommandApi
    {
        readonly object _gate = new();
        readonly List<Event> _log = new();
        readonly List<ulong> _pools = new();
        readonly Dictionary<ulong, int> _poolIndex = new();
        readonly Dictionary<ulong, int> _bufferPool = new();
        readonly List<ulong> _destroyed = new();
        readonly List<(ulong Buffer, ulong Value)> _submissions = new();

        ulong _nextPool = 0x1000;
        ulong _nextBuffer = 0x2000;
        VulkanSubmitStatus? _oneShotStatus;

        /// <summary>Every call in order, as text. The device-free stand-in for a native call log.</summary>
        internal IReadOnlyList<string> Events
        {
            get { lock (_gate) return _log.Select(e => e.Text).ToArray(); }
        }

        /// <summary>Pools created, in creation order.</summary>
        internal IReadOnlyList<ulong> Pools
        {
            get { lock (_gate) return _pools.ToArray(); }
        }

        /// <summary>Pools destroyed, in destruction order. A pool here twice is a double destroy.</summary>
        internal IReadOnlyList<ulong> Destroyed
        {
            get { lock (_gate) return _destroyed.ToArray(); }
        }

        /// <summary>Every <c>(commandBuffer, signalValue)</c> a submit was asked for, in call order, INCLUDING the
        /// ones that then failed. Submit order is what this list IS, which is the whole observable of the
        /// recording-contract test.</summary>
        internal IReadOnlyList<(ulong Buffer, ulong Value)> Submissions
        {
            get { lock (_gate) return _submissions.ToArray(); }
        }

        /// <summary>What every submit returns unless <see cref="FailNextSubmit"/> has armed a one-shot.</summary>
        internal VulkanSubmitStatus SubmitStatus { get; set; } = VulkanSubmitStatus.Success;

        /// <summary>The token a failing submit reports.</summary>
        internal string FailureToken { get; set; } = "VK_ERROR_OUT_OF_DEVICE_MEMORY";

        /// <summary>Run at the top of every submit, before the status is decided. A test that needs the device to
        /// die mid-submit hangs it here.</summary>
        internal Action? OnSubmit { get; set; }

        /// <summary>Make the NEXT submit fail with <paramref name="status"/>, and every one after it behave as
        /// <see cref="SubmitStatus"/> says. One-shot, so a test can submit successfully, fail exactly once, and
        /// submit successfully again without racing a property.</summary>
        internal void FailNextSubmit(VulkanSubmitStatus status)
        {
            lock (_gate) _oneShotStatus = status;
        }

        /// <inheritdoc/>
        public ulong CreatePool()
        {
            lock (_gate)
            {
                ulong pool = _nextPool++;
                int index = _pools.Count;
                _pools.Add(pool);
                _poolIndex[pool] = index;
                Record(index, $"CreatePool -> p{index}");
                return pool;
            }
        }

        /// <inheritdoc/>
        public ulong AllocatePrimaryBuffer(ulong pool)
        {
            lock (_gate)
            {
                ulong buffer = _nextBuffer++;
                int index = _poolIndex[pool];
                _bufferPool[buffer] = index;
                Record(index, $"AllocateBuffer(p{index}) -> b{index}");
                return buffer;
            }
        }

        /// <inheritdoc/>
        public void ResetPool(ulong pool)
        {
            lock (_gate)
            {
                int index = _poolIndex[pool];
                Record(index, $"ResetPool(p{index})");
            }
        }

        /// <inheritdoc/>
        public void BeginOneTimeSubmit(ulong commandBuffer)
        {
            lock (_gate)
            {
                int index = _bufferPool[commandBuffer];
                Record(index, $"Begin(b{index})");
            }
        }

        /// <inheritdoc/>
        public void EndRecording(ulong commandBuffer)
        {
            lock (_gate)
            {
                int index = _bufferPool[commandBuffer];
                Record(index, $"End(b{index})");
            }
        }

        /// <inheritdoc/>
        public VulkanSubmitStatus Submit(ulong commandBuffer, ulong signalValue, out string? failure)
        {
            failure = null;
            OnSubmit?.Invoke();

            lock (_gate)
            {
                int index = _bufferPool[commandBuffer];
                _submissions.Add((commandBuffer, signalValue));
                Record(index, $"Submit(b{index},{signalValue})");

                VulkanSubmitStatus status = _oneShotStatus ?? SubmitStatus;
                _oneShotStatus = null;

                if (status == VulkanSubmitStatus.Failed) failure = FailureToken;
                return status;
            }
        }

        /// <inheritdoc/>
        public void DestroyPool(ulong pool)
        {
            lock (_gate)
            {
                int index = _poolIndex[pool];
                _destroyed.Add(pool);
                Record(index, $"DestroyPool(p{index})");
            }
        }

        /// <summary>Every event belonging to <paramref name="pool"/> or its one buffer, in order. The per-pool
        /// trace the recording-contract test asserts against, and the unit row 11 extends when a record starts
        /// carrying real commands.</summary>
        internal IReadOnlyList<string> EventsForPool(ulong pool)
        {
            lock (_gate)
            {
                int index = _poolIndex[pool];
                return _log.Where(e => e.Pool == index).Select(e => e.Text).ToArray();
            }
        }

        /// <summary>The pool index a test's expected trace is written against. Pools and their one buffer share
        /// it, so a trace reads <c>p0</c> and <c>b0</c> rather than two unrelated numbers.</summary>
        internal int IndexOf(ulong pool)
        {
            lock (_gate) return _poolIndex[pool];
        }

        // Called with the lock held.
        void Record(int pool, string text) => _log.Add(new Event(pool, text));

        readonly struct Event
        {
            internal Event(int pool, string text)
            {
                Pool = pool;
                Text = text;
            }

            internal int Pool { get; }

            internal string Text { get; }
        }
    }
}
