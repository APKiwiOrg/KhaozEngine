namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>The device's completion boundaries for deferred native destruction.</summary>
    internal sealed unsafe partial class VulkanGpuDevice
    {
        /// <summary>
        /// Run every held destroy the timeline has passed at the present boundary, leaving unfinished work held.
        /// <see cref="Present"/> calls this after <see cref="VulkanRingAllocator.BeginFrame"/> and outside every
        /// lock. Idle waits use their own proven cutoff, and teardown drains after its device-wide wait.
        /// </summary>
        internal int DrainRetiredResources()
        {
            if (_liveness.IsDead) return 0;

            return _retired.Drain(_timeline.CompletedValue);
        }

        /// <summary>
        /// Wait for submitted work and destroy resources retired before this call, without requiring a present.
        /// The counted wait is <c>vkWaitSemaphores</c> on the submitted high-water (V-F4), with no queue lock held
        /// while waiting. Teardown still uses <c>vkDeviceWaitIdle</c> for the whole device.
        /// <para>
        /// The allocation high-water is captured under the submit lock after flushing setup work. Taking that
        /// lock makes any submission that already allocated a value finish registering success or failure before
        /// the snapshot. The subsequent timeline wait therefore covers every accepted submission through this
        /// cutoff, including when failed submissions left unreachable gaps in the counter. A newer submission
        /// can proceed during the wait, and resources retired against its value stay held.
        /// </para>
        /// <para>
        /// Two passes cover the terminal resource destroy and the memory chunk it may retire, matching the
        /// retirement depth teardown relies on. Both run outside the submit lock and skip native destruction on
        /// device loss. The second pass uses the same cutoff, so a newer allocation value cannot be mistaken for
        /// completion merely because the earlier wait finished.
        /// </para>
        /// <para>
        /// A dead device is a safe no-op (V-F10). Native results are checked in every configuration and device
        /// loss is latched at its own call site (V-G4). See <see cref="VulkanTimeline.WaitForIdle"/> for which
        /// waits count toward <c>DrainCount</c> and <c>DrainMs</c>.
        /// </para>
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;

            // A device-level read flushes creation-time clears and transitions before waiting (V-M10).
            FlushSetup();

            ulong retiredThrough;
            lock (_submitLock) retiredThrough = _timeline.LastAllocated;

            _timeline.WaitForIdle();

            if (!_liveness.IsDead)
            {
                _retired.Drain(retiredThrough);
                if (!_liveness.IsDead) _retired.Drain(retiredThrough);
            }

            // Validation raised by work this call flushed is reported by the same call, after its wait.
            _instance?.Value.Messenger?.Pump.ThrowIfLatched("WaitForIdle");
        }
    }
}
