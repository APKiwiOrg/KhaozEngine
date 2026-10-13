using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Last frame's foliage state for the temporal variant: its block, its packing and how a scene carries it
/// from one submission of a batch to the next.</summary>
public sealed class FoliageMotionUniformsTests
{
    [Fact]
    public void TheMotionBlockMirrorsTheStructAndFitsTheSlot()
    {
        Assert.Equal(["vec4 FocusRadius", "vec4 Density", "vec4 FadeWind", "vec4 WindTime", "vec4 Interactors[4]",
                "vec4 Strengths", "vec4 WindFade", "vec4 Agitation", "vec4 PrevFocus", "vec4 PrevInteractors[4]", "vec4 PrevStrengths", "vec4 PrevAgitation"],
            MotionUboLayoutTests.Members(ShaderSources.FoliageMotionVert, "uniform Foliage {"));
        Assert.Equal(192, (int)Marshal.OffsetOf<FoliageUniforms>(nameof(FoliageUniforms.PrevInteractor0)));
        Assert.Equal(256, (int)Marshal.OffsetOf<FoliageUniforms>(nameof(FoliageUniforms.PrevStrengths)));
        Assert.Equal(288, Marshal.SizeOf<FoliageUniforms>());
        Assert.Equal(512u, FoliageUniforms.SlotBytes);
    }

    [Fact]
    public void WithPreviousCarriesLastFramesFocusClockInteractorsAndStrengths()
    {
        var settings = new FoliageRenderSettings();
        FoliageUniforms last = FoliageUniforms.Build(new Vector3(1f, 2f, 3f), settings,
            new[] { new FoliageInteractor(new Vector3(4f, 0f, 5f), 2f, .5f) }, 7f);
        FoliageUniforms now = FoliageUniforms.Build(new Vector3(9f, 0f, 9f), settings, ReadOnlySpan<FoliageInteractor>.Empty, 8f);

        FoliageUniforms carried = now.WithPrevious(last);

        Assert.Equal(new Vector4(1f, 2f, 3f, 7f), carried.PrevFocus);
        Assert.Equal(new Vector4(4f, 0f, 5f, 2f), carried.PrevInteractor0);
        Assert.Equal(new Vector4(.5f, 0f, 0f, 0f), carried.PrevStrengths);
        Assert.Equal(now.FocusRadius, carried.FocusRadius);   // this frame's half is untouched
    }

    [Fact]
    public void ThePixelScaleOverloadWritesLastFramesScaleBesideThisFrames()
    {
        var slots = new FoliageUniforms[2];
        FoliageUniforms.ApplyPixelScale(slots, .02f, .03f);
        Assert.All(slots, s => Assert.Equal((.02f, .03f), (s.WindFade.Y, s.WindFade.Z)));
    }

    [Fact]
    public void ASceneFoldsEachBatchsLastFrameIntoItsSubmissionOnlyWhileTemporal()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        MeshHandle blade = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(blade, Matrix4x4.Identity, .1f) });
        var settings = new FoliageRenderSettings { DistantDensity = 1f };
        var near = new FoliageInteractor(new Vector3(1f, 0f, 0f), 2f, 1f, .4f);

        scene.Begin();
        scene.EffectTimeSeconds = 1f;
        scene.DrawFoliage(batch, new Vector3(3f, 0f, 4f), settings, new[] { near });
        Assert.Equal(Vector4.Zero, scene.FoliageUniformsForTests[0].PrevFocus);   // temporal off: the base bytes alone
        Assert.Equal(new Vector4(.4f, 0f, 0f, 0f), scene.FoliageUniformsForTests[0].Agitation);
        Assert.Equal(Vector4.Zero, scene.FoliageUniformsForTests[0].PrevAgitation);

        scene.ForceTemporalForTests = true;
        scene.Begin();
        scene.EffectTimeSeconds = 2f;
        scene.DrawFoliage(batch, new Vector3(3f, 0f, 4f), settings, new[] { near });
        Assert.Equal(new Vector4(3f, 0f, 4f, 2f), scene.FoliageUniformsForTests[0].PrevFocus);   // no last frame: its own
        Assert.Equal(new Vector4(.4f, 0f, 0f, 0f), scene.FoliageUniformsForTests[0].PrevAgitation);

        scene.Begin();
        scene.EffectTimeSeconds = 2.5f;
        scene.DrawFoliage(batch, new Vector3(5f, 0f, 6f), settings);
        Assert.Equal(new Vector4(3f, 0f, 4f, 2f), scene.FoliageUniformsForTests[0].PrevFocus);
        Assert.Equal(new Vector4(1f, 0f, 0f, 2f), scene.FoliageUniformsForTests[0].PrevInteractor0);
        Assert.Equal(Vector4.Zero, scene.FoliageUniformsForTests[0].Interactor0);   // this frame has none
        Assert.Equal(Vector4.Zero, scene.FoliageUniformsForTests[0].Agitation);
        Assert.Equal(new Vector4(.4f, 0f, 0f, 0f), scene.FoliageUniformsForTests[0].PrevAgitation);

        scene.Begin(); // skip one batch frame
        scene.Begin();
        scene.DrawFoliage(batch, Vector3.Zero, settings, [near with { Agitation = .8f }]);
        Assert.Equal(new Vector4(.8f, 0f, 0f, 0f), scene.FoliageUniformsForTests[0].PrevAgitation);
    }

    [Fact]
    public void AgitationOffsetsAndHistoryAreIndependent()
    {
        Assert.Equal(160, (int)Marshal.OffsetOf<FoliageUniforms>("Agitation"));
        Assert.Equal(176, (int)Marshal.OffsetOf<FoliageUniforms>("PrevFocus"));
        Assert.Equal(192, (int)Marshal.OffsetOf<FoliageUniforms>("PrevInteractor0"));
        Assert.Equal(256, (int)Marshal.OffsetOf<FoliageUniforms>("PrevStrengths"));
        Assert.Equal(272, (int)Marshal.OffsetOf<FoliageUniforms>("PrevAgitation"));
        Assert.Equal(288, Marshal.SizeOf<FoliageUniforms>());
        Assert.Equal(512u, FoliageUniforms.SlotBytes);
        var actor = new FoliageInteractor(Vector3.Zero, 2f);
        var settings = new FoliageRenderSettings();
        FoliageUniforms last = FoliageUniforms.Build(Vector3.Zero, settings,
            [actor with { Agitation = .2f }, actor with { Agitation = .4f }, actor with { Agitation = .6f }, actor with { Agitation = 1f }], 0f);
        FoliageUniforms current = FoliageUniforms.Build(Vector3.Zero, settings,
            [actor with { Agitation = 1f }, actor, actor with { Agitation = .3f }, actor with { Agitation = .5f }], 1f);
        Assert.Equal(new Vector4(1f, 0f, .3f, .5f), current.WithPrevious(last).Agitation);
        Assert.Equal(new Vector4(.2f, .4f, .6f, 1f), current.WithPrevious(last).PrevAgitation);
        FoliageUniforms single = FoliageUniforms.Build(Vector3.Zero, settings, [actor with { Agitation = .2f }], 0f);
        Assert.Equal(new Vector4(.2f, 0f, 0f, 0f), single.Agitation);
        Assert.Equal(Vector4.Zero, single.PrevAgitation);
        Assert.Equal(Vector4.Zero, FoliageUniforms.Build(Vector3.Zero, settings, [], 0f).Agitation);
    }

    [Fact]
    public void TurningTemporalOffAndOnWithinASubmittedFrameResetsAgitationHistory()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        MeshHandle blade = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = scene.CreateFoliageBatch([new FoliageInstance(blade, Matrix4x4.Identity, .1f)]);
        var settings = new FoliageRenderSettings { DistantDensity = 1f };
        var actor = new FoliageInteractor(Vector3.Zero, 2f, 1f, .2f);
        scene.Begin();
        scene.DrawFoliage(batch, Vector3.Zero, settings, [actor]);
        harness.Render();
        scene.Begin();
        scene.DrawFoliage(batch, Vector3.Zero, settings, [actor with { Agitation = .4f }]);
        Assert.Equal(new Vector4(.2f, 0f, 0f, 0f), scene.FoliageUniformsForTests[0].PrevAgitation);
        harness.Render();

        scene.Begin();
        scene.ForceTemporalForTests = false;
        scene.DrawFoliage(batch, Vector3.Zero, settings, [actor with { Agitation = .8f }]);
        Assert.Equal(Vector4.Zero, scene.FoliageUniformsForTests[0].PrevAgitation);
        scene.ForceTemporalForTests = true;
        harness.Render();
        Assert.Equal(new Vector4(.8f, 0f, 0f, 0f), scene.FoliageUniformsForTests[0].PrevAgitation);
        Assert.Equal(scene.FoliageUniformsForTests[0].Agitation, scene.FoliageUniformsForTests[0].PrevAgitation);
    }
}
