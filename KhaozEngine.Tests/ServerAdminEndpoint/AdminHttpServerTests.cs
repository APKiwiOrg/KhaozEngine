using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

public class AdminHttpServerTests
{
    private static float Flat(float x, float z) => 0f;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Requires_Bearer_And_Serves_Online()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("alice"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);

        var admin = new ServerAdmin(server);
        int port = FreePort();
        var opts = new AdminEndpointOptions
        {
            Port = port,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler);
        string baseUrl = $"https://127.0.0.1:{port}/admin";

        HttpResponseMessage noAuth = await hc.GetAsync(baseUrl + "/online");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage ok = await hc.GetAsync(baseUrl + "/online");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        string body = await ok.Content.ReadAsStringAsync();
        Assert.Contains("alice", body);

        await http.StopAsync();
    }
}
