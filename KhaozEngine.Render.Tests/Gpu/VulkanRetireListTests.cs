using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decision V-F9, the deferred-disposal retire list: a native destroy held back until the device timeline has
    /// passed the value of the last submission that could still be reading what is being destroyed.
    /// <para>
    /// DRIVEN WITH FAKE DESTROYS, because no resource type exists on this backend yet. Buffers, textures and
    /// samplers are row 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519) and command pools are row 7
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517). The list is generic over a callback precisely so
    /// both rows can hand it their own destroy without it learning either one, and these rows assert the RELEASE
    /// RULE, which is the part neither of those rows should have to re-derive.
    /// </para>
    /// </summary>
    public sealed class VulkanRetireListTests
    {
        /// <summary>Nothing is destroyed before the timeline passes its value. This is the whole point of the type
        /// and the one row that would catch it being inverted.</summary>
        [Fact]
        public void NothingIsDestroyedBeforeItsValueHasPassed()
        {
            var destroyed = new List<string>();
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(5, () => destroyed.Add("five"));

            Assert.Equal(0, list.Drain(0));
            Assert.Equal(0, list.Drain(4));
            Assert.Empty(destroyed);
            Assert.Equal(1, list.Count);

            Assert.Equal(1, list.Drain(5));
            Assert.Equal(new[] { "five" }, destroyed);
            Assert.Equal(0, list.Count);
        }

        /// <summary>A value of 0 means nothing had ever been submitted when the resource was disposed, so the very
        /// next drain releases it. That is correct rather than a special case: a resource no submission has ever
        /// referenced is safe to destroy immediately.</summary>
        [Fact]
        public void AnEntryRetiredBeforeAnySubmission_IsReleasedByTheNextDrain()
        {
            var destroyed = 0;
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(0, () => destroyed++);

            Assert.Equal(1, list.Drain(0));
            Assert.Equal(1, destroyed);
        }

        /// <summary>
        /// OUT-OF-ORDER RETIRE VALUES ARE ORDINARY, so a drain scans the WHOLE list rather than stopping at the
        /// first entry whose value has not passed. Values are allocated by the submit path and resources are
        /// retired by whoever disposes them, so a resource retired later can easily carry a lower value. A drain
        /// that stopped early would strand every entry behind the first unready one, which on a long run reads as
        /// a memory regression rather than as a bug in a drain.
        /// </summary>
        [Fact]
        public void OutOfOrderValues_ReleaseIndependentlyOfRetireOrder()
        {
            var destroyed = new List<string>();
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(9, () => destroyed.Add("nine"));
            list.Retire(2, () => destroyed.Add("two"));
            list.Retire(7, () => destroyed.Add("seven"));

            Assert.Equal(2, list.Drain(7));
            Assert.Equal(new[] { "two", "seven" }, destroyed);
            Assert.Equal(1, list.Count);

            Assert.Equal(1, list.Drain(9));
            Assert.Equal(new[] { "two", "seven", "nine" }, destroyed);
        }

        /// <summary>Destroys run in the order they were retired, which is the order a reader of a native call log
        /// expects. The scan walks backwards so removal cannot disturb the indices it has yet to visit, and this
        /// row is what stops that implementation detail leaking into the order callbacks see.</summary>
        [Fact]
        public void DestroysRunInRetireOrder()
        {
            var destroyed = new List<int>();
            var list = new VulkanRetireList(new RecordingLogger());

            for (int i = 0; i < 6; i++)
            {
                int captured = i;
                list.Retire(1, () => destroyed.Add(captured));
            }

            Assert.Equal(6, list.Drain(1));
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, destroyed);
        }

        /// <summary>A second drain at the same value finds nothing left, because everything ready was removed by
        /// the first. Draining is safe at any cadence, which is what lets the frame boundary call it
        /// unconditionally.</summary>
        [Fact]
        public void DrainingTwice_IsIdempotent()
        {
            var destroyed = 0;
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(3, () => destroyed++);

            Assert.Equal(1, list.Drain(3));
            Assert.Equal(0, list.Drain(3));
            Assert.Equal(0, list.Drain(100));
            Assert.Equal(1, destroyed);
        }

        /// <summary>
        /// THE TEARDOWN DRAIN RUNS EVERYTHING, whatever its value. It is legal in exactly one place, the device's
        /// own <c>Dispose</c> after <c>vkDeviceWaitIdle</c> has returned, and at that point the GPU is idle by
        /// definition so every recorded value has been passed and the values have nothing left to say.
        /// </summary>
        [Fact]
        public void DrainAll_DestroysEverythingRegardlessOfValue()
        {
            var destroyed = new List<string>();
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(1, () => destroyed.Add("one"));
            list.Retire(4_000, () => destroyed.Add("far"));
            list.Retire(ulong.MaxValue, () => destroyed.Add("never"));

            Assert.Equal(3, list.DrainAll());
            Assert.Equal(new[] { "one", "far", "never" }, destroyed);
            Assert.Equal(0, list.Count);
            Assert.Equal(0, list.DrainAll());
        }

        /// <summary>
        /// ABANDON DROPS WITHOUT RUNNING, for the one case where running would be the bug: the device is already
        /// DEAD, so <c>vkDestroyDevice</c> or the loss that killed it already destroyed every object made from it,
        /// and a destroy call now is a call against freed memory, which aborts the process through the Vulkan
        /// loader rather than failing quietly.
        /// </summary>
        [Fact]
        public void Abandon_DropsEveryDestroyWithoutRunningIt()
        {
            var destroyed = 0;
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(1, () => destroyed++);
            list.Retire(2, () => destroyed++);

            Assert.Equal(2, list.Abandon());
            Assert.Equal(0, destroyed);
            Assert.Equal(0, list.Count);
        }

        /// <summary>
        /// A THROWING DESTROY IS LOGGED AND THE DRAIN CARRIES ON. The teardown drain runs between
        /// <c>vkDeviceWaitIdle</c> and <c>vkDestroyDevice</c>, so a callback that threw its way out of the loop
        /// would take the device destroy with it and leak the whole device plus every driver allocation behind it.
        /// One failed destroy leaks one object, which is the smaller of the two.
        /// </summary>
        [Fact]
        public void AThrowingDestroy_IsSwallowedAndTheRestStillRun()
        {
            var logger = new RecordingLogger();
            var destroyed = new List<string>();
            var list = new VulkanRetireList(logger);

            list.Retire(1, () => destroyed.Add("before"));
            list.Retire(1, () => throw new InvalidOperationException("the driver said no"));
            list.Retire(1, () => destroyed.Add("after"));

            Assert.Equal(3, list.DrainAll());
            Assert.Equal(new[] { "before", "after" }, destroyed);
            Assert.Single(logger.Warns);
            Assert.Contains("InvalidOperationException", logger.Warns[0], StringComparison.Ordinal);
        }

        /// <summary>A destroy that retires something else appends to a list nobody is iterating, because the ready
        /// entries are taken off under the lock and invoked after it is released. The appended entry survives to
        /// the next drain rather than being swallowed or throwing.</summary>
        [Fact]
        public void ADestroyThatRetiresSomethingElse_IsHeldForTheNextDrain()
        {
            var destroyed = new List<string>();
            var list = new VulkanRetireList(new RecordingLogger());

            list.Retire(1, () =>
            {
                destroyed.Add("parent");
                list.Retire(1, () => destroyed.Add("child"));
            });

            Assert.Equal(1, list.Drain(1));
            Assert.Equal(new[] { "parent" }, destroyed);
            Assert.Equal(1, list.Count);

            Assert.Equal(1, list.Drain(1));
            Assert.Equal(new[] { "parent", "child" }, destroyed);
        }
    }
}
