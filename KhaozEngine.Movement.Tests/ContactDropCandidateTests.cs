using System;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropCandidateTests
{
    [Fact]
    public void StopsAtNearestColumnAndChoosesHighestPermittedRestingSurface()
    {
        var top = Grid([4, null, null, null]);
        var low = Grid([null, 0, null, null]);
        var mid = Grid([null, 2, null, null]);
        NavGrid[] grids = [top, low, mid];
        var links = ContactDropCandidates.Generate(grids, Layers(grids), ContactDropFixture.Tuning, 6);
        Assert.Equal(new NavLink(0, 0, 0, 2, 1, 0) { Kind = NavLinkKind.Drop }, Assert.Single(links));
    }

    [Fact]
    public void SupportedContinuationAndAcceptedExitStopTheBrinkScan()
    {
        NavGrid[] grids = [Grid([4, 4, 0])];
        Assert.DoesNotContain(ContactDropCandidates.Generate(grids, Layers(grids), ContactDropFixture.Tuning, 6),
            link => link.FromX == 0);
        grids = [Grid([4, 0])];
        var layer = new NavTraversalLayer(2, 1, [true, true], [1, 0]);
        Assert.Empty(ContactDropCandidates.Generate(grids, [layer], ContactDropFixture.Tuning, 6));
    }

    [Fact]
    public void HorizonAndDoubleHeightBudgetAreExact()
    {
        NavGrid[] grids = [Grid([6, null, 0])];
        Assert.Single(ContactDropCandidates.Generate(grids, Layers(grids), ContactDropFixture.Tuning, 6));
        Assert.Empty(ContactDropCandidates.Generate(grids, Layers(grids), ContactDropFixture.Tuning, MathF.BitDecrement(6)));
        grids = [Grid([6, null, null, 0])];
        Assert.Empty(ContactDropCandidates.Generate(grids, Layers(grids), ContactDropFixture.Tuning, 6));
    }

    internal static NavGrid Grid(float?[] cells) => NavGrid.FromBlockedSurfaces(cells.Length, 1, 0.5f, 0, 0,
        cells.Select(y => !y.HasValue).ToArray(), cells.Select(y => y ?? 0).ToArray())
        .WithRestingHeights(cells.Select(y => y ?? 0).ToArray());

    internal static NavTraversalLayer[] Layers(NavGrid[] grids) => grids.Select(grid =>
        new NavTraversalLayer(grid.Width, grid.Height,
            Enumerable.Range(0, grid.Width).Select(x => grid.RestingHeightAt(x, 0).HasValue).ToArray(),
            new byte[grid.Width])).ToArray();
}
