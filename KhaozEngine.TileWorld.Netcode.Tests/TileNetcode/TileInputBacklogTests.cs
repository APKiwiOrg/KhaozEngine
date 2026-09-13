using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// One client frame stall must not leave the server applying that client's input late for the rest of the session
/// (#874). The client sends ONE command for a frame that covered many ticks rather than a burst, because the server
/// already synthesised the missed ones, so no backlog forms and the server never trails prediction afterwards.
/// </summary>
public class TileInputBacklogTests
{
    const float Tick = TileCombatHarness.Tick;
    static readonly TileCoord Spawn = new(10, 10, 0);

    static TileCombatHarness Join()
    {
        var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), Spawn, clientPhase: 0.13f);
        h.Frames(6);
        Assert.True(h.Client.IsJoined);
        return h;
    }

    [Fact]
    public void A_stalled_frame_sends_one_command_and_the_server_never_trails_afterwards()
    {
        using TileCombatHarness h = Join();
        int before = h.Client.PendingCommandCount;

        // The server keeps ticking while the client's frame is frozen, which is what an in-process server does
        // through a render hitch and a remote one does through any stall.
        const int frozenTicks = 6;
        for (int i = 0; i < frozenTicks; i++) { h.Server.Poll(); h.Server.Tick(Tick); }
        float frozen = frozenTicks * Tick;
        h.Client.Tick(frozen);
        h.Client.Poll();
        h.Client.AdvancePresentation(frozen);

        // One command for six missed ticks, not six.
        Assert.Equal(before + 1, h.Client.PendingCommandCount);

        h.Frames(40);
        Assert.True(h.Server.InputDepth(0) <= 1, $"server backlog {h.Server.InputDepth(0)}");
        Assert.True(h.Client.PendingCommandCount <= 1, $"client pending {h.Client.PendingCommandCount}");
        Assert.Equal(0, h.Client.SnapCount);
    }
}
