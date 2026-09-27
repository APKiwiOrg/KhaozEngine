using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A STEADY DRAIN OF THE RETIRE LIST ALLOCATES NOTHING, device-free. <see cref="VulkanRetireList.Drain"/> runs
    /// at every <c>Present</c>, so a drain that finds nothing due is part of every steady windowed frame.
    /// <para>
    /// <b>THE DRAIN USED TO BUILD A CLOSURE PER CALL.</b> It handed its release loop a lambda capturing the completed
    /// value, which cost a closure and a delegate, 88 bytes, on every present whether or not anything was due.
    /// </para>
    /// <para>
    /// <b>HELD ENTRIES ARE IN THE LIST</b>, carrying values the measured drains never reach, so every drain scans real
    /// entries and releases none. That is the steady shape: a resource disposed a few frames ago waits there while
    /// the frames go by.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class VulkanRetireListAllocationTests
    {
        const int HeldEntries = 4, Drains = 256;
        const ulong HeldValue = 1_000_000;

        [Fact]
        public void ASteadyDrainAllocatesNothing()
        {
            var list = new VulkanRetireList(new RecordingLogger());
            int destroyed = 0;
            for (int i = 0; i < HeldEntries; i++) list.Retire(HeldValue + (ulong)i, () => destroyed++);
            ulong completed = 0;
            int released = 0;

            void Frames(int count)
            {
                for (int i = 0; i < count; i++) released += list.Drain(++completed);
            }

            Frames(Drains);

            AllocAssert.NoPerCallAllocation($"{Drains} drains past {HeldEntries} held entries", () => Frames(Drains));

            Assert.Equal(0, released);
            Assert.Equal(0, destroyed);
            Assert.Equal(HeldEntries, list.Count);
            Assert.True(completed < HeldValue, "a measured drain reached a held value, so it measured a release");
        }
    }
}
