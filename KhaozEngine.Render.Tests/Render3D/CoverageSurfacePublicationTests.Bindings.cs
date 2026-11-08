using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageSurfacePublicationTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(true, 2, true)]
    public void Reordered_actors_bind_their_actual_surfaces_and_materials_in_every_pass(bool gpu, int visibility, bool motion)
    {
        using var fixture = new CoverageMultiActorFixture();
        fixture.Scene.UseGpuSkinning = gpu;
        fixture.Scene.ForceTemporalForTests = motion;
        fixture.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        fixture.Scene.Post.Quality.Shadows.PointShadows.MaxDynamicLightsPerFrame = 2;
        if (visibility == 2)
        {
            fixture.Scene.FrustumCulling = true;
            fixture.Second.Model = Matrix4x4.CreateTranslation(fixture.Scene.Camera.Eye + new Vector3(100, 0, 0));
        }
        var read = new CoverageRecordedBindings(fixture);
        fixture.Begin(); fixture.Queue(fixture.First); fixture.Queue(fixture.Second, shadowOnly: visibility == 1);
        AddActorLights(fixture); fixture.Record();
        var previous = read.Outputs();
        Assert.Equal(2, previous.Count);
        var firstModel = fixture.First.Model; var secondModel = fixture.Second.Model;
        fixture.Submit();
        fixture.First.Model.M42 += .25f; fixture.Second.Model.M42 += .5f;
        fixture.Begin(); fixture.Queue(fixture.Second, shadowOnly: visibility == 1); fixture.Queue(fixture.First);
        AddActorLights(fixture); fixture.Record();
        var current = read.Outputs(); var draws = read.Draws();
        Assert.Equal(2, current.Count); Assert.NotEmpty(draws);
        foreach (var actor in new[] { fixture.First, fixture.Second })
        {
            var actorDraws = draws.Where(d => d.Model == actor.Model).ToArray();
            Assert.NotEmpty(actorDraws);
            var required = actor == fixture.First || visibility == 0
                ? new[] { motion ? "motion" : "colour-depth", "cascade", "point", "full", "visible" }
                : new[] { "cascade", "point", "full", "visible" };
            foreach (string pass in required)
            {
                var passDraws = actorDraws.Where(d => d.Pass == pass).ToArray();
                Assert.NotEmpty(passDraws);
                Assert.Equal(actor.Meshes.Length, passDraws.Select(d => d.FirstIndex).Distinct().Count());
            }
            if (actor == fixture.Second && visibility != 0)
                Assert.DoesNotContain(actorDraws, d => d.Pass is "motion" or "colour-depth");
            Assert.All(actorDraws, d => read.AssertActor(d, actor, current[actor.Key], previous[actor.Key],
                actor == fixture.First ? firstModel : secondModel));
        }
        Assert.Equal(draws.Length, draws.Count(d => d.Model == fixture.First.Model || d.Model == fixture.Second.Model));
        Assert.NotEqual(current[fixture.First.Key].Base, current[fixture.Second.Key].Base);
        Assert.NotSame(draws.First(d => d.Model == fixture.First.Model).Indices,
            draws.First(d => d.Model == fixture.Second.Model).Indices);
        fixture.Submit();
    }

    static void AddActorLights(CoverageMultiActorFixture fixture)
    {
        foreach (var actor in new[] { fixture.First, fixture.Second })
            fixture.Scene.AddLight(new Vector3(actor.Model.M41, actor.Model.M42 + 1, actor.Model.M43),
                Color.White, 10, 1, LightShadow.Dynamic);
    }
}
