using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

/// <summary>
/// <see cref="ServerAdmin"/> and the HTTPS endpoint over a <see cref="TileWorldServer"/> (#826), with no adapter in
/// between: the tile head implements <see cref="IAdminControllable"/> itself, so the facade and the endpoint take it
/// exactly as they take a <c>WorldServer</c>.
/// </summary>
public class TileWorldServerAdminEndpointTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A tile server and one client that joined with a token, pumped on the test thread.</summary>
    internal sealed class TileRig : IDisposable
    {
        public const float Tick = 0.25f;
        private const float Frame = 0.05f;
        private static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

        public readonly InMemoryTransportHub Hub = new();
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        public readonly List<string> Notices = new();
        private float serverAccum;

        public TileRig(string account, IBanStore? bans = null)
        {
            Server = new TileWorldServer(Hub.Server, new TileWorldServerConfig
            {
                TickSeconds = Tick,
                StepTicks = Ticks,
                Spawn = new TileCoord(5, 5, 0),
                MaxPlayers = 8,
                BanStore = bans,
            }, OpenMap());
            Client = new TileWorldClient(Hub.CreateClient(), new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = Ticks,
            }, OpenMap(), connectToken: Encoding.UTF8.GetBytes(account));
            Client.NoticeReceived += Notices.Add;
            Client.Tick(0.13f);
            Client.Poll();
            Frames(16);
            Assert.True(Client.IsJoined);
        }

        // One region, every tile open on every plane.
        public static TileCollisionMap OpenMap()
        {
            var map = new TileCollisionMap(TileWorldDocument.DefaultPlaneCount);
            map.EnsureRegion(new RegionCoord(0, 0));
            return map;
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Client.Tick(Frame);
                Server.Poll();
                serverAccum += Frame;
                while (serverAccum >= Tick)
                {
                    serverAccum -= Tick;
                    Server.Tick(Tick);
                }
                Client.Poll();
                Client.AdvancePresentation(Frame);
            }
        }

        public void Dispose() { Client.Dispose(); Server.Dispose(); Hub.Dispose(); }
    }

    [Fact]
    public void ServerAdmin_drives_a_tile_world_server_end_to_end()
    {
        using var rig = new TileRig("acct-a");
        rig.Server.SpawnPlayer(3, "acct-b", "Bea");
        rig.Server.SetPlayerState(3, TileMoveState.At(new TileCoord(20, 30, 0), TileDirection.S), teleport: true);
        var admin = new ServerAdmin(rig.Server);
        rig.Frames(8);

        IReadOnlyList<OnlinePlayer> online = admin.ListOnline();
        Assert.Equal(new[] { "acct-a", "acct-b" }, online.Select(p => p.AccountId).OrderBy(a => a, StringComparer.Ordinal));
        OnlinePlayer b = online.Single(p => p.AccountId == "acct-b");
        Assert.Equal("Bea", b.DisplayName);

        admin.Broadcast("game:hello");
        rig.Frames(8);
        Assert.Equal(new[] { "game:hello" }, rig.Notices);

        // Sent to where Bea stands, read straight off the list.
        admin.Teleport(PlayerRef.Account("acct-a"), b.Position);
        rig.Frames(12);
        Assert.True(rig.Server.TryGetPlayerState(0, out TileMoveState a));
        Assert.Equal(new TileCoord(20, 30, 0), a.Tile);
        Assert.Equal(new TileCoord(20, 30, 0), rig.Client.Prediction.PredictedState.Tile);

        admin.Kick(PlayerRef.Account("acct-a"), "game:bye");
        rig.Frames(8);
        Assert.Equal(new[] { "game:hello", "game:bye" }, rig.Notices);
        Assert.Equal(1, rig.Server.PlayerCount);
        Assert.Equal("acct-b", Assert.Single(admin.ListOnline()).AccountId);
    }

    // #1104: one store at the tile door and behind it. ServerAdmin.BanAsync records the ban and kicks, the kick
    // reads as the ban (the same ke:banned the door refuses with), and lifting it opens the door again.
    [Fact]
    public async Task ServerAdmin_BanAsync_ends_a_tile_session_with_the_ban_token()
    {
        var bans = new InMemoryBanStore();
        using var rig = new TileRig("acct-ban", bans);
        var admin = new ServerAdmin(rig.Server, bans);

        await admin.BanAsync("acct-ban", "griefing");
        rig.Frames(8);

        Assert.Equal(new[] { TileServerReason.Banned }, rig.Notices);
        Assert.Equal(HandshakeToken.BannedReason, TileServerReason.Banned);
        Assert.Equal(0, rig.Server.PlayerCount);
        Assert.False(rig.Client.IsJoined);
        Assert.Equal("griefing", Assert.Single(admin.ListBans()).Reason);

        await admin.UnbanAsync("acct-ban");
        Assert.Empty(admin.ListBans());
    }

    [Fact]
    public async Task The_https_endpoint_serves_a_tile_world_server()
    {
        using var rig = new TileRig("acct-http");
        var admin = new ServerAdmin(rig.Server);
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler) { Timeout = RequestTimeout };
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        string baseUrl = $"https://127.0.0.1:{http.BoundPort}/admin";

        string listed = await hc.GetStringAsync(baseUrl + "/online");
        Assert.Contains("acct-http", listed, StringComparison.Ordinal);

        // A broadcast the tile wire cannot carry is the operator naming the wrong thing: a 400 with the reason.
        using (var empty = new StringContent("{\"Text\":\"\"}", Encoding.UTF8, "application/json"))
        {
            HttpResponseMessage refused = await hc.PostAsync(baseUrl + "/broadcast", empty);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("reason token", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (var token = new StringContent("{\"Text\":\"game:hi\"}", Encoding.UTF8, "application/json"))
            Assert.Equal(HttpStatusCode.Accepted, (await hc.PostAsync(baseUrl + "/broadcast", token)).StatusCode);

        Vector3 target = new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight).PoseAt(new TileCoord(9, 12, 0)).Position;
        string teleport = string.Format(CultureInfo.InvariantCulture,
            "{{\"Account\":\"acct-http\",\"X\":{0},\"Y\":{1},\"Z\":{2}}}", target.X, target.Y, target.Z);
        using (var move = new StringContent(teleport, Encoding.UTF8, "application/json"))
            Assert.Equal(HttpStatusCode.Accepted, (await hc.PostAsync(baseUrl + "/teleport", move)).StatusCode);

        rig.Frames(12);
        Assert.Equal(new[] { "game:hi" }, rig.Notices);
        Assert.True(rig.Server.TryGetPlayerState(0, out TileMoveState moved));
        Assert.Equal(new TileCoord(9, 12, 0), moved.Tile);

        await http.StopAsync();
    }
}
