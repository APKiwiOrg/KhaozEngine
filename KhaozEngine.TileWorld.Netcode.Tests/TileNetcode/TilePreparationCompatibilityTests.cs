using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationCompatibilityTests
{
    [Fact]
    public void Legacy_mode_retains_its_first_hit_and_protocol()
    {
        using var f = new PreparationDeliveryScenario(enabled: false);
        (long attacker, long target) = f.Fight();
        long eligible = f.Server.TickCount;
        var server = new List<TileCombatEvent>();
        var client = new List<TileCombatEvent>();
        var prepared = new List<PreparedCombatEvent>();
        f.Server.OnCombatEvent += server.Add;
        f.Client.CombatEvent += client.Add;
        f.Client.PreparedCombatEvent += prepared.Add;
        f.Wire.Sent.Clear();
        f.Step();
        Assert.Equal(eligible, Assert.Single(f.Rules.Rolls).Tick);
        Assert.Equal(new byte[] { 0, 3 }, f.Payloads().Select(x => x[0]));
        Assert.Equal(server, client);
        Assert.Single(client);
        Assert.Empty(prepared);
        Assert.False(f.Server.TryGetCombatPreparation(attacker, out _));
        Assert.Equal(-1d, f.Client.CombatPresentationTick);
        Assert.True(f.Server.TryGetHealth(target, out var health));
        Assert.Equal(995, health.Current);
        f.Through(eligible + 14);
        Assert.Equal(2, client.Count);
        Assert.Equal(14L, f.Rules.Rolls[1].Tick - f.Rules.Rolls[0].Tick);
        Assert.DoesNotContain(f.Payloads(), frame => frame[0] is 4 or 5);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void Consumer_protocol_version_refuses_mixed_preparation_modes(bool serverPrepared, bool clientPrepared, bool admitted)
    {
        // The consumer chooses distinct versions. The engine does not infer an incorrect matching version string.
        string serverVersion = serverPrepared ? "example-prepared-2" : "example-legacy-1";
        string clientVersion = clientPrepared ? "example-prepared-2" : "example-legacy-1";
        var hub = new InMemoryTransportHub();
        var document = TileMoveSimulatorTests.FlatWorld();
        var map = TileMoveSimulatorTests.Bake(document);
        var gate = ConnectionGate.Wrap(new AllowAllAuthenticator(), serverVersion, "same-content");
        using var server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
            { CombatPreparationRules = serverPrepared ? new PreparationScenario.Profiles() : null }, map, null, gate);
        using INetTransport transport = hub.CreateClient();
        using var client = new TileWorldClient(transport,
            new TileWorldClientConfig { TickSeconds = .25f, StepTicks = new TileStepTicks(4, 2), CombatPreparationEnabled = clientPrepared }, map,
            connectToken: TileProtocol.BuildConnectToken(clientVersion, "same-content", null));
        client.Tick(.06f);
        client.Poll();
        server.Poll();
        client.Poll();
        Assert.Equal(admitted, client.IsJoined);
        Assert.Equal(admitted ? 1 : 0, server.PlayerCount);
        if (admitted) Assert.Null(client.RefusedReason);
        else
        {
            Assert.NotNull(client.RefusedReason);
            Assert.True(HandshakeToken.TryParseIncompatibleVersion(client.RefusedReason, out string required));
            Assert.Equal(serverVersion, required);
        }
    }
}
