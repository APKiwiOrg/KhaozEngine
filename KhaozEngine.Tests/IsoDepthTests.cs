using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

public class IsoDepthTests
{
    [Fact]
    public void Greater_wx_plus_wy_sorts_later()
    {
        IsoDepthKey near = IsoDepth.DepthKey(0f, 0f);
        IsoDepthKey far = IsoDepth.DepthKey(2f, 3f);
        Assert.True(near < far);
    }

    [Fact]
    public void Higher_z_sorts_later_at_the_same_tile()
    {
        IsoDepthKey ground = IsoDepth.DepthKey(1f, 1f, z: 0f);
        IsoDepthKey raised = IsoDepth.DepthKey(1f, 1f, z: 2f);
        Assert.True(ground < raised);
    }

    [Fact]
    public void Layer_breaks_ties_at_equal_depth()
    {
        IsoDepthKey under = IsoDepth.DepthKey(1f, 1f, z: 0f, layer: 0);
        IsoDepthKey over = IsoDepth.DepthKey(1f, 1f, z: 0f, layer: 5);
        Assert.True(under < over);
        Assert.Equal(under.Depth, over.Depth);
    }

    [Fact]
    public void Sorting_a_draw_list_paints_far_to_near()
    {
        // Unsorted draw list of (tile, layer) tagged with an id.
        var items = new[]
        {
            (id: "near-unit", key: IsoDepth.DepthKey(3f, 3f, layer: 1)),
            (id: "far-floor", key: IsoDepth.DepthKey(0f, 0f, layer: 0)),
            (id: "near-floor", key: IsoDepth.DepthKey(3f, 3f, layer: 0)),
            (id: "mid", key: IsoDepth.DepthKey(1f, 2f, layer: 0)),
        };

        List<string> order = items.OrderBy(i => i.key).Select(i => i.id).ToList();

        Assert.Equal(new[] { "far-floor", "mid", "near-floor", "near-unit" }, order);
    }

    [Fact]
    public void Equal_keys_compare_equal()
    {
        Assert.Equal(IsoDepth.DepthKey(2f, 1f, 1f, 3), IsoDepth.DepthKey(2f, 1f, 1f, 3));
        Assert.Equal(0, IsoDepth.DepthKey(2f, 1f, 1f, 3).CompareTo(IsoDepth.DepthKey(2f, 1f, 1f, 3)));
    }

    [Fact]
    public void Default_zWeight_is_one()
    {
        Assert.Equal(IsoDepth.DepthKey(1f, 1f, 2f, 0, zWeight: 1f), IsoDepth.DepthKey(1f, 1f, 2f));
    }

    [Fact]
    public void Higher_zWeight_pushes_a_tall_stack_in_front_of_a_nearer_neighbour()
    {
        IsoDepthKey nearLow = IsoDepth.DepthKey(3f, 3f, z: 0f);                  // depth 6
        IsoDepthKey farTallW1 = IsoDepth.DepthKey(1f, 1f, z: 3f, zWeight: 1f);   // depth 2 + 3 = 5
        IsoDepthKey farTallW2 = IsoDepth.DepthKey(1f, 1f, z: 3f, zWeight: 2f);   // depth 2 + 6 = 8

        Assert.True(nearLow > farTallW1);  // at weight 1 the nearer-but-lower tile draws in front
        Assert.True(farTallW2 > nearLow);  // raising zWeight pushes the tall stack in front instead
    }
}
