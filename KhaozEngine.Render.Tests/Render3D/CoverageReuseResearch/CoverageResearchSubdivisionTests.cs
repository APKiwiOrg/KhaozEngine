using System;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

public sealed class CoverageResearchSubdivisionTests
{
    [Fact]
    public void Subdivision_preserves_the_posed_triangle_instead_of_reskinning_its_interpolated_bind_position()
    {
        Assert.Equal(new Vector3(.5f, 1, 0), CoverageResearchSubdivision.Interpolate(
            new(0, 0, 0), new(2, 0, 0), new(0, 2, 0), [.25, .25, .5]));
    }

    [Fact]
    public void Subdivision_refuses_recipes_that_do_not_describe_a_point_in_the_original_triangle()
    {
        Assert.Throws<ArgumentException>(() => CoverageResearchSubdivision.Interpolate(default, default, default, [-.1, .6, .5]));
        Assert.Throws<ArgumentException>(() => CoverageResearchSubdivision.Interpolate(default, default, default, [.2, .2, .2]));
    }
}
