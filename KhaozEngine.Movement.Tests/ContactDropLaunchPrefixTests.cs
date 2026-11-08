using System;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropLaunchPrefixTests
{
    [Theory]
    [InlineData(4f, false)]
    [InlineData(6f, true)]
    public void AHiddenStepInTheLaunchPrefixCountsAgainstTheEntireDescent(float allowance, bool expected)
    {
        using var fixture = new ContactDropFixture(install: (world, builder) =>
            ContactNavigationFixture.Box(world, builder, "brink-step", new(0, 4, -1), new(0.2f, 4.2f, 1)));
        var ground = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        int sourceLayer = -1, targetLayer = -1;
        for (int layer = 0; layer < ground.Space.Layers.Count; layer++)
        {
            if (ground.Space.Layers[layer].RestingHeightAt(2, 1) == 4) sourceLayer = layer;
            if (ground.Space.Layers[layer].RestingHeightAt(4, 1) == 0) targetLayer = layer;
        }
        Assert.NotEqual(-1, sourceLayer);
        Assert.NotEqual(-1, targetLayer);
        float peak = 4;
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            DropStep = step => { if (step.Grounded) peak = MathF.Max(peak, step.Feet.Y); },
        });
        using var source = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning);
        using var proof = new ContactNavigationProof(fixture.Capture, fixture.Context, source, ground.Footprint,
            maxDropMetres: allowance);
        bool accepted = proof.TryDrop(ground.Space,
            new NavLink(sourceLayer, 2, 1, targetLayer, 4, 1) { Kind = NavLinkKind.Drop });
        Assert.True(peak > 4);
        Assert.Equal(expected, accepted);
    }
}
