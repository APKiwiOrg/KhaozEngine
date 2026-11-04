using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainEvidenceSubsetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DependencySubsetPreservesTheRecipeAndPhysicalCertificate(bool rim)
    {
        using var fixture = new ContactTerrainPolicyFixture(rim ? 4 : 0);
        var e = fixture.Capture.DescentEvidence!;
        var reads = new ContactTerrainEvidenceReads(e);
        KhaozEngine.Navigation.NavLink[] original;
        using (reads.Observe())
        {
            original = ContactTerrainDropCandidates.Generate(fixture.Navigation, e, fixture.Sources,
                ContactTerrainScene.Tuning, 6).ToArray();
            if (rim) Assert.True(fixture.Prove().Accepted);
        }
        var subset = ContactTerrainEvidenceSubset.Build(e, reads, ContactTerrainScene.Tuning);
        Assert.True(subset.FaceCount < e.FaceCount);
        var sources = new ContactTerrainProfileEvidence(subset, fixture.Navigation);
        var regenerated = ContactTerrainDropCandidates.Generate(fixture.Navigation, subset, sources, ContactTerrainScene.Tuning, 6);
        Assert.Equal(original, regenerated.ToArray());
        if (rim)
        {
            using var binding = fixture.Scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
            using var proof = new ContactNavigationProof(fixture.Capture, fixture.Scene.Context, binding, fixture.Navigation.Footprint, maxDropMetres: 6);
            proof.EnableTerrainDescents(subset, sources);
            Assert.True(proof.TryDrop(fixture.Navigation.Space, fixture.Link), proof.TerrainRefusal.ToString());
        }
        else
        {
            Assert.Empty(regenerated);
            Assert.Equal(0, subset.FaceCount);
            Assert.Equal(0, subset.RawCount);
            Assert.Equal(0, subset.ReferenceCount);
            Assert.Equal(0, sources.Count);
        }
    }
}
