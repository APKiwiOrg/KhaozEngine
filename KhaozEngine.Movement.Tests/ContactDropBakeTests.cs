using System;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ContactDropBakeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void RealContactFallPublishesOnlyDescendingLinks(float height)
    {
        using var fixture = new ContactDropFixture(height);
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            DropStep = step => output.WriteLine($"{step.Source} run={step.Run} hold={step.Hold} {step.Outcome} feet={step.Feet} grounded={step.Grounded}"),
        });
        GroundNavigation nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default, DropOptions(6));
        output.WriteLine($"candidates={nav.Space.Links.Count} anchors={string.Join(',', nav.Space.Layers.SelectMany(grid => Enumerable.Range(0, grid.Width).Select(x => grid.RestingHeightAt(x, 1))))}");
        NavLink[] drops = nav.Graph.Links.Where(link => link.Kind.ToString() == "Drop").ToArray();
        Assert.NotEmpty(drops);
        foreach (NavLink drop in drops)
        {
            Assert.True(nav.Space.Layers[drop.FromLayer].RestingHeightAt(drop.FromX, drop.FromZ) >
                nav.Space.Layers[drop.ToLayer].RestingHeightAt(drop.ToX, drop.ToZ));
            Assert.DoesNotContain(drops, other => other.FromLayer == drop.ToLayer && other.FromX == drop.ToX &&
                other.FromZ == drop.ToZ && other.ToLayer == drop.FromLayer && other.ToX == drop.FromX && other.ToZ == drop.FromZ);
        }
    }

    [Fact]
    public void ZeroAllowancePreservesGroundTopologyAndBeyondAllowanceNeverLaunches()
    {
        using var fixture = new ContactDropFixture(MathF.BitIncrement(6));
        GroundNavigation disabled = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        GroundNavigation enabled = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default, DropOptions(6));
        ContactNavigationFixture.SameGraphBits(disabled, enabled);
    }

    internal static GroundProfileOptions DropOptions(float allowance)
    {
        var options = new GroundProfileOptions();
        var property = typeof(GroundProfileOptions).GetProperty("MaxDropMetres");
        Assert.NotNull(property);
        property.SetValue(options, allowance);
        return options;
    }
}
