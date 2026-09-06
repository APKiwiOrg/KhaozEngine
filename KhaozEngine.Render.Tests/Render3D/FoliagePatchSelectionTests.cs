using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliagePatchSelectionTests
{
    static FoliagePatchLayout At(float x, params float[] ranks) => FoliagePatchLayout.Build(
        ranks.Select(rank => new FoliageInstance(new MeshHandle(4), Matrix4x4.CreateTranslation(x, 0f, 0f), rank)).ToArray(),
        _ => new MeshBounds(Vector3.Zero, Vector3.One));

    [Theory]
    [InlineData(0f, 4)]
    [InlineData(15f, 3)]
    [InlineData(20f, 2)]
    [InlineData(25f, 2)]
    [InlineData(30f, 2)]
    [InlineData(30.1f, 0)]
    public void RankPrefixesTrackTheNearestRootAndKeepTheDistantBoundary(float x, int expected)
    {
        FoliagePatchLayout layout = At(x, .1f, .2f, .5f, .9f);
        var settings = new FoliageRenderSettings { DrawRadius = 30f, DensityRadius = 20f, FadeBandWidth = 10f, DistantDensity = .2f };

        Assert.Equal(expected, layout.Patches[0].CandidateCount(layout.Instances, Vector3.Zero, settings));
    }

    [Fact]
    public void SelectionUsesTheClosestRootInThePatchAndIgnoresFocusHeight()
    {
        FoliageInstance[] input =
        [
            new(new MeshHandle(4), Matrix4x4.CreateTranslation(8f, 0f, 0f), .1f),
            new(new MeshHandle(4), Matrix4x4.CreateTranslation(15f, 0f, 0f), .9f),
        ];
        FoliagePatchLayout layout = FoliagePatchLayout.Build(input, _ => new MeshBounds(Vector3.Zero, Vector3.One));
        var settings = new FoliageRenderSettings { DrawRadius = 9f, FadeBandWidth = 0f };

        Assert.Equal(2, layout.Patches[0].CandidateCount(layout.Instances, new Vector3(0f, 1000f, 0f), settings));
    }

    [Fact]
    public void DisabledQualityAndExactQualityRanksAreCulled()
    {
        FoliagePatchLayout layout = At(0f, 0f, .1f, .5f, .9f);

        Assert.Equal(0, layout.Patches[0].CandidateCount(layout.Instances, Vector3.Zero,
            new FoliageRenderSettings { QualityDensity = 0f }));
        Assert.Equal(2, layout.Patches[0].CandidateCount(layout.Instances, Vector3.Zero,
            new FoliageRenderSettings { QualityDensity = .5f }));
    }

    [Fact]
    public void ZeroFadeBandStillThinsBeyondTheDensityRadius()
    {
        FoliagePatchLayout layout = At(20f, .1f, .2f, .9f);
        var settings = new FoliageRenderSettings { DrawRadius = 40f, DensityRadius = 10f, FadeBandWidth = 0f, DistantDensity = .2f };

        Assert.Equal(2, layout.Patches[0].CandidateCount(layout.Instances, Vector3.Zero, settings));
    }

    [Fact]
    public void ABladeAtItsExactPersonalCutoffRemainsACandidate()
    {
        FoliagePatchLayout layout = At(15f, .1f, .5f, .7f);
        var settings = new FoliageRenderSettings { DrawRadius = 20f, FadeBandWidth = 10f, QualityDensity = .8f, DistantDensity = .2f };

        Assert.Equal(2, layout.Patches[0].CandidateCount(layout.Instances, Vector3.Zero, settings));
    }

    [Fact]
    public void PatchesConservativelyRetainEveryExistingCpuPlacement()
    {
        var random = new Random(8314);
        FoliageInstance[] input = Enumerable.Range(0, 800).Select(_ => new FoliageInstance(new MeshHandle(4),
            Matrix4x4.CreateTranslation(random.Next(-1000, 1000) / 10f, random.Next(-30, 30), random.Next(-1000, 1000) / 10f),
            random.Next(0, 1000) / 1000f)).ToArray();
        FoliagePatchLayout layout = FoliagePatchLayout.Build(input, _ => new MeshBounds(Vector3.Zero, Vector3.One));
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes =
            new Dictionary<string, IReadOnlyList<MeshHandle>> { ["grass"] = [new MeshHandle(4)] };
        FoliageRenderSettings[] policies =
        [
            new(),
            new() { DrawRadius = 64f, DensityRadius = 32f, FadeBandWidth = 20f, DistantDensity = .1f },
            new() { DrawRadius = 80f, DensityRadius = 10f, FadeBandWidth = 0f, QualityDensity = .4f, DistantDensity = .8f },
            new() { DrawRadius = 30f, FadeBandWidth = 100f, QualityDensity = .8f, DistantDensity = 0f },
        ];

        foreach (FoliageRenderSettings settings in policies)
        foreach (Vector3 focus in new[] { Vector3.Zero, new Vector3(31.3f, 50f, -26.9f) })
        {
            var candidates = new HashSet<Matrix4x4>();
            foreach (FoliagePatch patch in layout.Patches)
            {
                int count = patch.CandidateCount(layout.Instances, focus, settings);
                for (int i = patch.Start; i < patch.Start + count; i++) candidates.Add(layout.Instances[i].Transform);
            }
            var queued = new SceneInstances();
            GroundCoverRenderer.Queue(queued, input.Select(i => new GroundCoverInstance("grass", i.Transform.Translation,
                i.Transform, i.ThinningRank)).ToArray(), meshes, focus, new GroundCoverRenderOptions
            {
                DrawRadius = settings.DrawRadius,
                DensityRadius = settings.DensityRadius,
                FadeBandWidth = settings.FadeBandWidth,
                InstanceFadeBandWidth = settings.InstanceFadeBandWidth,
                QualityDensity = settings.QualityDensity,
                DistantDensity = settings.DistantDensity,
            });

            Assert.NotEmpty(queued.Items);
            Assert.All(queued.Items, instance => Assert.Contains(instance.World, candidates));
        }
    }
}
