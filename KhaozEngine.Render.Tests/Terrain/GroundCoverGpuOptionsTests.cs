using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class GroundCoverGpuOptionsTests
{
    [Theory]
    [InlineData(false, true, GroundCoverFadeMode.HeightScale, false, false)]
    [InlineData(true, false, GroundCoverFadeMode.HeightScale, false, false)]
    [InlineData(true, true, GroundCoverFadeMode.Dissolve, false, false)]
    [InlineData(true, true, GroundCoverFadeMode.HeightScale, true, false)]
    [InlineData(true, true, GroundCoverFadeMode.HeightScale, false, true)]
    public void RetentionRequiresImmutableHeightScaledUnshadowedCover(
        bool requested, bool immutable, GroundCoverFadeMode fadeMode, bool shadows, bool expected)
    {
        IReadOnlyList<GroundCoverInstance> cover = Array.Empty<GroundCoverInstance>();
        if (immutable) cover = new GroundCoverBatch(cover);
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = requested, FadeMode = fadeMode, CastsShadows = shadows,
        };

        Assert.Equal(expected, GroundCoverGpuCache.CanRetain(cover, options));
    }

    [Theory]
    [InlineData(GroundCoverFadeMode.Dissolve, false)]
    [InlineData(GroundCoverFadeMode.HeightScale, true)]
    public void CpuQueuePreservesFadeAndShadowPolicyWhenGpuBatchesAreRequested(
        GroundCoverFadeMode mode, bool shadows)
    {
        var cover = new GroundCoverBatch([new GroundCoverInstance("grass", new Vector3(15f, 0f, 0f),
            Matrix4x4.CreateTranslation(15f, 0f, 0f), .1f)]);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["grass"] = [new MeshHandle(4, 1)] };
        var queue = new SceneInstances();

        int drawn = GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero, new GroundCoverRenderOptions
        {
            UseGpuBatches = true, FadeMode = mode, CastsShadows = shadows, DrawRadius = 20f,
            FadeBandWidth = 10f, DistantDensity = 1f,
        });

        Assert.Equal(1, drawn);
        SceneInstances.Instance submitted = Assert.Single(queue.Items);
        Assert.Equal(shadows, submitted.CastsShadows);
        Assert.Equal(mode == GroundCoverFadeMode.Dissolve ? .5f : 0f, submitted.DissolveThreshold);
        Assert.Equal(mode == GroundCoverFadeMode.HeightScale ? .5f : 1f, submitted.World.M22);
        Assert.Equal(new Vector3(15f, 0f, 0f), submitted.World.Translation);
    }

    [Fact]
    public void SubmissionSnapshotsLiveOptionsAndInteractorValues()
    {
        var actors = new List<FoliageInteractor> { new(new Vector3(1f, 2f, 3f), 4f, .5f) };
        var options = new GroundCoverRenderOptions
        {
            DrawRadius = 88f, DensityRadius = 40f, FadeBandWidth = 13f, InstanceFadeBandWidth = 2f,
            QualityDensity = .8f, DistantDensity = .2f, WindDirection = new Vector2(.3f, -.8f),
            WindStrength = .5f, WindSpeed = 3f, WindSpatialFrequency = .75f, Interactors = actors,
        };
        Span<FoliageInteractor> interactors = stackalloc FoliageInteractor[4];

        FoliageRenderSettings settings = GroundCoverGpuCache.ReadOptions(options, Vector3.Zero, interactors, out int count);
        options.DrawRadius = 1f;
        actors[0] = new FoliageInteractor(Vector3.Zero, 0f);

        Assert.Equal(88f, settings.DrawRadius);
        Assert.Equal(40f, settings.DensityRadius);
        Assert.Equal(13f, settings.FadeBandWidth);
        Assert.Equal(2f, settings.InstanceFadeBandWidth);
        Assert.Equal(.8f, settings.QualityDensity);
        Assert.Equal(.2f, settings.DistantDensity);
        Assert.Equal(new Vector2(.3f, -.8f), settings.WindDirection);
        Assert.Equal(.5f, settings.WindStrength);
        Assert.Equal(3f, settings.WindSpeed);
        Assert.Equal(.75f, settings.WindSpatialFrequency);
        Assert.Equal(1, count);
        Assert.Equal(new FoliageInteractor(new Vector3(1f, 2f, 3f), 4f, .5f), interactors[0]);
    }

    [Theory]
    [InlineData("radius")]
    [InlineData("density radius")]
    [InlineData("fade")]
    [InlineData("instance fade")]
    [InlineData("quality")]
    [InlineData("distant density")]
    [InlineData("wind direction")]
    [InlineData("wind strength")]
    [InlineData("wind speed")]
    [InlineData("wind frequency")]
    public void RetainedOptionsRejectInvalidSettingsBeforeCreatingResources(string setting)
    {
        var options = new GroundCoverRenderOptions();
        switch (setting)
        {
            case "radius": options.DrawRadius = float.NaN; break;
            case "density radius": options.DensityRadius = -1f; break;
            case "fade": options.FadeBandWidth = float.PositiveInfinity; break;
            case "instance fade": options.InstanceFadeBandWidth = -1f; break;
            case "quality": options.QualityDensity = 2f; break;
            case "distant density": options.DistantDensity = -1f; break;
            case "wind direction": options.WindDirection = new Vector2(float.NaN, 0f); break;
            case "wind strength": options.WindStrength = 2f; break;
            case "wind speed": options.WindSpeed = -1f; break;
            case "wind frequency": options.WindSpatialFrequency = float.NaN; break;
        }

        Assert.ThrowsAny<ArgumentException>(() => Read(options));
    }

    [Fact]
    public void RetainedSubmissionRejectsInvalidFocusAndInteractors()
    {
        var options = new GroundCoverRenderOptions();
        Assert.ThrowsAny<ArgumentException>(() => Read(options, new Vector3(0f, float.NaN, 0f)));
        options.Interactors = null!;
        Assert.ThrowsAny<ArgumentException>(() => Read(options));
        options.Interactors = new FoliageInteractor[5];
        Assert.ThrowsAny<ArgumentException>(() => Read(options));
        options.Interactors = [new FoliageInteractor(Vector3.Zero, -1f)];
        Assert.ThrowsAny<ArgumentException>(() => Read(options));
        options.Interactors = [new FoliageInteractor(new Vector3(float.NaN, 0f, 0f), 1f)];
        Assert.ThrowsAny<ArgumentException>(() => Read(options));
        options.Interactors = [new FoliageInteractor(Vector3.Zero, 1f, 2f)];
        Assert.ThrowsAny<ArgumentException>(() => Read(options));
    }

    static FoliageRenderSettings Read(GroundCoverRenderOptions options, Vector3 focus = default) =>
        GroundCoverGpuCache.ReadOptions(options, focus, new FoliageInteractor[4], out _);
}
