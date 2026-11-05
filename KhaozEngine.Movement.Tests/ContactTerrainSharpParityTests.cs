using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactTerrainSharpParityTests
{
    [Fact]
    public void OptedInEmptyTerrainEvidenceKeepsTheExactVerticalBoxProof()
    {
        using var fixture = new ContactDropFixture();
        var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var link = nav.Graph.Links.First(l => l.Kind == NavLinkKind.Drop);
        int width = fixture.Capture.Columns.Width, height = fixture.Capture.Columns.Height;
        var empty = new PhysicsNavDescentEvidence(fixture.Capture.Options, width, height, [], new int[width * height + 1], [],
            Enumerable.Repeat(DescentCoverage.Excluded, width * height).ToArray(), [], []);
        var sources = new ContactTerrainProfileEvidence(empty, nav);
        var before = new List<ContactDropStepObservation>();
        var after = new List<ContactDropStepObservation>();
        Assert.True(Prove(false, before));
        Assert.True(Prove(true, after));
        Assert.NotEmpty(before);
        Assert.Equal(before.ToArray(), after.ToArray());

        bool Prove(bool terrain, List<ContactDropStepObservation> steps)
        {
            using var hooks = ContactNavigationBuildHooks.Install(new() { DropStep = steps.Add });
            using var binding = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning);
            using var proof = new ContactNavigationProof(fixture.Capture, fixture.Context, binding, nav.Footprint, maxDropMetres: 6);
            if (terrain) proof.EnableTerrainDescents(empty, sources);
            return proof.TryDrop(nav.Space, link);
        }
    }
}
