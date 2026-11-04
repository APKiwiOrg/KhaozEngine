using System;
using System.IO;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainGoldenTests
{
    [Fact]
    public void MacBakedFormatFourLoadsAndReconstructsEveryAcceptedTerrainCertificate()
    {
        // #1697. The same committed Mac artifact is exercised on all three hosted operating systems.
        using var stream = typeof(ContactTerrainGoldenTests).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Fixtures.ContactTerrainFormatFour.navbake")!;
        using var original = new MemoryStream(); stream.CopyTo(original); original.Position = 0;
        var options = ContactTerrainOptInTests.Options with { CaptureTerrainDescentEvidence = true };
        var profiles = new[] { new NavBakeProfile("player", ContactTerrainScene.Tuning, default)
            { MaxDropMetres = 6, AllowTerrainDescents = true } };
        var loaded = GroundNavigationBake.Load(original, new(options, new NavBakeSources().Add("world", new byte[32]), profiles), "22.0.1");
        Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
        var nav = loaded.Bake!.GetProfile("player");
        var terrain = nav.Graph.Links.Where(link => nav.Terrain!.Route(link) == ContactDropRoute.Terrain).ToArray();
        Assert.Equal(8, terrain.Length);
        foreach (var link in terrain)
        {
            Assert.True(nav.Terrain!.TryGet(link, out var certificate));
            Assert.NotNull(certificate);
        }
        using var rewritten = new MemoryStream(); loaded.Bake.WriteTo(rewritten);
        Assert.Equal(original.ToArray(), rewritten.ToArray());
    }
}
