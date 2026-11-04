using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainPersistenceTests
{
    [Theory]
    [InlineData(1.3f)]
    [InlineData(4f)]
    [InlineData(6f)]
    public void FormatFourRegeneratesTheFreshTerrainCandidatesAndAcceptedLinks(float height)
    {
        using var scene = new ContactTerrainScene(height);
        var options = ContactTerrainOptInTests.Options with { CaptureTerrainDescentEvidence = true };
        var profiles = new[] { new NavBakeProfile("player", ContactTerrainScene.Tuning, default)
            { MaxDropMetres = 6, AllowTerrainDescents = true }, new NavBakeProfile("creature", ContactTerrainScene.Tuning, default) };
        var sources = new NavBakeSources().Add("world", new byte[32]);
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        var bake = GroundNavigationBake.Create(capture, sources, profiles);
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        byte[] bytes = stream.ToArray();
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        stream.Position = 0;
        var loaded = GroundNavigationBake.Load(stream, new(options, sources, profiles));
        Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
        var fresh = bake.GetProfile("player"); var restored = loaded.Bake!.GetProfile("player");
        Assert.True(restored.AllowTerrainDescents);
        Assert.Equal(fresh.Space.Links.ToArray(), restored.Space.Links.ToArray());
        Assert.Equal(fresh.Graph.Links.ToArray(), restored.Graph.Links.ToArray());
        Assert.Contains(restored.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        Assert.False(loaded.Bake.GetProfile("creature").AllowTerrainDescents);
        Assert.DoesNotContain(loaded.Bake.GetProfile("creature").Space.Links, link => link.Kind == NavLinkKind.Drop);
        using var rewritten = new MemoryStream(); loaded.Bake.WriteTo(rewritten);
        Assert.Equal(bytes, rewritten.ToArray());
        using var binding = scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
        using var proof = new ContactNavigationProof(capture, scene.Context, binding, restored.Footprint, maxDropMetres: 6);
        proof.EnableTerrainDescents(restored.Terrain!.Evidence, restored.Terrain.Sources);
        Assert.NotEmpty(fresh.Space.Links);
        foreach (var link in restored.Space.Links.Where(link => link.Kind == NavLinkKind.Drop))
            Assert.Equal(fresh.Graph.Links.Contains(link), proof.TryDrop(restored.Space, link));
    }
}
