using KhaozEngine.ItemInstances;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Server.Items;

/// <summary>
/// The parity spec 3.6 asks for. <see cref="InstanceIdAllocator"/> MIRRORS
/// <c>NetIdAllocator.cs:14-70</c>'s packing rather than calling it, because an instance id is a Foundation
/// concern and <c>KhaozEngine.Replication</c> is a Server package, so the two copies of the arithmetic can
/// drift. This is the only project that references both, so this is where they are held together.
/// <para>
/// The counters are deliberately NOT compared. A net id and an instance id are different spaces that must
/// not share one, which is the whole reason there are two allocators. Only the PACKING is shared.
/// </para>
/// </summary>
public class InstanceIdPackingParityTests
{
    public static TheoryData<ushort, long> Packings() => new()
    {
        { 0, 1 },
        { 0, 268_435_455 },
        { 1, 1 },
        { 7, 4201 },
        { 1234, 999_999 },
        { 65535, 1 },
        { 65535, 4201 },
        { 65535, InstanceIdAllocator.MaxCounter },
    };

    [Fact]
    public void The_four_constants_agree()
    {
        Assert.Equal(NetIdAllocator.CounterBits, InstanceIdAllocator.CounterBits);
        Assert.Equal(NetIdAllocator.NodeBits, InstanceIdAllocator.NodeBits);
        Assert.Equal(NetIdAllocator.CounterMask, InstanceIdAllocator.CounterMask);
        Assert.Equal(NetIdAllocator.MaxNodeId, InstanceIdAllocator.MaxNodeId);
        Assert.Equal(NetIdAllocator.MaxCounter, InstanceIdAllocator.MaxCounter);
    }

    [Theory]
    [MemberData(nameof(Packings))]
    public void Pack_NodeOf_and_CounterOf_produce_identical_values(ushort nodeId, long counter)
    {
        long theirs = NetIdAllocator.Pack(nodeId, counter);
        long mine = InstanceIdAllocator.Pack(nodeId, counter);

        Assert.Equal(theirs, mine);
        Assert.Equal(NetIdAllocator.NodeOf(theirs), InstanceIdAllocator.NodeOf(mine));
        Assert.Equal(NetIdAllocator.CounterOf(theirs), InstanceIdAllocator.CounterOf(mine));
    }
}
