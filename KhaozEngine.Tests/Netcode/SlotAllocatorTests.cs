using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class SlotAllocatorTests
{
    [Fact]
    public void Allocate_HandsOutLowestFree_AndRecyclesOnRelease()
    {
        var alloc = new SlotAllocator(maxSlots: 3);
        Assert.True(alloc.TryAllocate(out int a)); Assert.Equal(0, a);
        Assert.True(alloc.TryAllocate(out int b)); Assert.Equal(1, b);
        alloc.Release(0);
        Assert.True(alloc.TryAllocate(out int c)); Assert.Equal(0, c); // 0 recycled, lowest free
    }

    [Fact]
    public void Allocate_WhenFull_Fails()
    {
        var alloc = new SlotAllocator(maxSlots: 1);
        Assert.True(alloc.TryAllocate(out _));
        Assert.False(alloc.TryAllocate(out int none));
        Assert.Equal(-1, none);
    }
}
