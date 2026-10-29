using System;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropTopologyProofTests
{
    [Theory]
    [InlineData(0.2f, 0.5f, 1)]
    [InlineData(0.3f, 0.25f, 3)]
    public void ActualFallProofAlsoCertifiesSameLayerAdjacentAndWiderEndpoints(float radius, float cellSize, int span)
    {
        var tuning = ContactDropFixture.Tuning with { CapsuleRadius = radius };
        var options = ContactDropFixture.Options with { CellSize = cellSize, MinZ = -1.125f, MaxZ = 1.125f };
        using var fixture = new ContactDropFixture(tuning: tuning, options: options);
        var ground = fixture.Capture.BuildProfile(tuning, default);
        NavGrid first = ground.Space.Layers[0];
        int cells = first.Width * first.Height;
        var accepted = new bool[cells];
        var raw = new float[cells];
        var resting = new float[cells];
        var exits = new byte[cells];
        for (int layer = 0; layer < ground.Space.Layers.Count; layer++)
            for (int z = 0; z < first.Height; z++)
                for (int x = 0; x < first.Width; x++)
                {
                    if (!ground.Graph.Layers[layer].IsAccepted(x, z)) continue;
                    int index = z * first.Width + x;
                    Assert.False(accepted[index]);
                    accepted[index] = true;
                    raw[index] = ground.Space.Layers[layer].SurfaceHeightAt(x, z)!.Value;
                    resting[index] = ground.Space.Layers[layer].RestingHeightAt(x, z)!.Value;
                    exits[index] = ground.Graph.Layers[layer].ExitMask(x, z);
                }
        NavGrid grid = NavGrid.FromBlockedSurfaces(first.Width, first.Height, first.CellSize,
            first.OriginX, first.OriginZ, accepted.Select(value => !value).ToArray(), raw).WithRestingHeights(resting);
        var traversal = new NavTraversalLayer(first.Width, first.Height, accepted, exits);
        var candidates = ContactDropCandidates.Generate([grid], [traversal], tuning, 6);
        var space = new NavSpace([grid], candidates);
        using var source = fixture.Context.CreateNavigationBinding(tuning);
        using var proof = new ContactNavigationProof(fixture.Capture, fixture.Context, source, ground.Footprint, maxDropMetres: 6);
        Assert.Contains(candidates, link => link.FromLayer == link.ToLayer && link.ToX - link.FromX == span && proof.TryDrop(space, link));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(1f, true)]
    public void AirControlMustReachTheNamedLandingBeyondTheErodedBrink(float airControl, bool expected)
    {
        var tuning = ContactDropFixture.Tuning with { CapsuleRadius = 0.3f, AirControl = airControl };
        var options = ContactDropFixture.Options with { CellSize = 0.25f, MinZ = -1.125f, MaxZ = 1.125f };
        using var fixture = new ContactDropFixture(tuning: tuning, options: options);
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Space.Links, link => link.Kind == NavLinkKind.Drop);
        Assert.Equal(expected, nav.Graph.Links.Any(link => link.Kind == NavLinkKind.Drop));
    }
}
