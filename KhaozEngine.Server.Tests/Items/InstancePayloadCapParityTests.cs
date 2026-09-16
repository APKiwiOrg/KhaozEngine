using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Server.Items;

/// <summary>
/// The payload cap, held across the two packages that each have to state it. Contracts 9.6 sets ONE number, 512,
/// and <c>KhaozEngine.TileWorld.Netcode</c> cannot reference the item packages to read it: the tile netcode
/// carries a drop's instance as an opaque long and opaque bytes, and a dependency on the packages that define
/// what is in them is exactly what the sibling component exists to avoid.
/// <para>So there are two constants, and this is the only project that sees both. A drift between them is the
/// failure worth catching: the spawn door and the page codec would disagree about which payloads are legal,
/// which shows up as a container the server accepts and the wire silently empties.</para>
/// </summary>
public class InstancePayloadCapParityTests
{
    [Fact]
    public void The_tile_wire_cap_and_the_slot_cap_are_one_number()
    {
        Assert.Equal(ItemSlot.MaxPayloadBytes, TileProtocol.MaxInstancePayloadBytes);
        Assert.Equal(ItemInstancePayload.MaxInstancePayloadBytes, TileProtocol.MaxInstancePayloadBytes);
        Assert.Equal(512, TileProtocol.MaxInstancePayloadBytes);
    }
}
