using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Theory]
    [InlineData("tile")]
    [InlineData("splat")]
    [InlineData("rigid")]
    [InlineData("skinnedCpu")]
    [InlineData("skinnedGpu")]
    [InlineData("all")]
    [InlineData("none")]
    [InlineData("tileChild")]
    public void Actual_shared_shadow_receivers_control_atlas_ownership_at_unit_outcome(string failing)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        using var recipe = RendererPerfRecipe.Create("empty");
        var frameError = new InvalidOperationException("original prepare");
        var targetError = new InvalidOperationException("independent target release");
        var childError = new InvalidOperationException("tile albedo after set release");
        string[] names = ["rigid", "skinnedCpu", "skinnedGpu", "splat", "tile", "laterTile"];
        int[] ordinals = failing == "all" ? [25, 30, 31, 33, 34] : [25, 30, 31, 34, 39];
        string[] kinds = ["meshMaterialSet", "eMaterialSet", "eSkinnedMaterialSet", "splat.set", "tileGround.set"];
        var errors = names.Take(5).Select(n => new InvalidOperationException(n + " receiver set release")).ToArray();
        bool[] refuse = names.Take(5).Select(n => failing == "all" || failing == n).ToArray();
        var releaseOrder = new List<string>();
        FakeResourceSet[] receivers = [];
        RendererPerfSceneWorkload? work = null;
        FakeTexture? albedo = null, modelDefault = null;
        FakeResourceSet? modelSet = null;
        SubmissionTestBuffer? modelUniform = null;
        PointShadowAtlas? baseAtlas = null, transientAtlas = null;
        int baseReleases = 0, transientReleases = 0;
        rig.WorkFactory = (device, _, observer) =>
        {
            work = new RendererPerfSceneWorkload(device, observer, CleanupIdentity(recipe));
            Scene3D scene = work.Scene;
            var texture = scene.LoadTexture([255, 255, 255, 255], 1, 1, TextureMipPolicy.None);
            albedo = (FakeTexture)Field<List<IGpuTexture>>(scene, "_textures").Last();
            scene.LoadMesh(MeshPrimitives.Box(1), texture);
            scene.LoadSkinnedMesh(SkinnedMeshBuilder.BuildTube(.5f, 1, 4, 4, 2, Axis.Z), texture);
            var layers = Enumerable.Range(0, SplatMaterialConfig.LayerCount)
                .Select(_ => new SplatLayerImage { AlbedoRgba = [255, 255, 255, 255], NormalRgba = [128, 128, 255, 255] }).ToList();
            scene.LoadSplatMaterial(1, 1, layers);
            scene.LoadTileGroundMaterial(1, 1, [new TileGroundLayerImage { AlbedoRgba = [255, 255, 255, 255] }]);
            scene.LoadTileGroundMaterial(1, 1, [new TileGroundLayerImage { AlbedoRgba = [255, 255, 255, 255] }]);
            var before = ShadowReceivers(scene);
            Assert.Equal(names.Length, before.Count);
            var replacement = Assert.IsType<Scene3D.PointShadowReplacement>(scene.BuildPointShadowReplacement(16, 2));
            replacement.TransientAtlas = Assert.IsType<PointShadowAtlas>(PointShadowAtlas.TryCreate(device, 16, 1));
            baseAtlas = replacement.Atlas; transientAtlas = replacement.TransientAtlas;
            // Exercise the actual immutable receiver rebuild and its scene commit, before atlas adoption.
            var result = (PointShadowBindResult)typeof(Scene3D).GetMethod("BindPointShadowAtlasesToReceivers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scene, [baseAtlas.Texture, transientAtlas.Texture])!;
            Assert.Equal(PointShadowBindResult.Rebound, result);
            scene.CommitPointShadowReplacement(replacement);
            Assert.False(((FakeTexture)baseAtlas.Texture).Disposed);
            Assert.False(((FakeTexture)transientAtlas.Texture).Disposed);
            receivers = ShadowReceivers(scene).Cast<FakeResourceSet>().ToArray();
            for (int i = 0; i < receivers.Length; i++)
            {
                Assert.NotSame(before[i], receivers[i]); Assert.False(((FakeResourceSet)before[i]).Disposed);
                Assert.Contains(receivers[i].Resources, r => ReferenceEquals(r, baseAtlas.Texture));
                Assert.Contains(receivers[i].Resources, r => ReferenceEquals(r, transientAtlas.Texture));
                int index = i;
                receivers[i].OnDispose = () =>
                {
                    Assert.True(scene.DisposalSafeForDependants);
                    releaseOrder.Add(names[index]);
                    if (index < refuse.Length && refuse[index]) throw errors[index];
                };
            }
            Assert.Same(baseAtlas.Texture, scene.BoundPointShadowTexture);
            Assert.Same(transientAtlas.Texture, scene.BoundPointShadowTransientTexture);
            var model = Field<ModelRenderer>(scene, "_model");
            modelUniform = Field<SubmissionTestBuffer>(model, "_ubo");
            modelDefault = Field<FakeTexture>(model, "_white");
            modelSet = Field<FakeResourceSet>(model, "_defaultSet");
            Assert.Contains(receivers[0].Resources, r => ReferenceEquals(r, modelUniform));
            Assert.Contains(modelSet.Resources, r => ReferenceEquals(r, baseAtlas.Texture));
            Assert.Contains(modelSet.Resources, r => ReferenceEquals(r, transientAtlas.Texture));
            ((FakeFramebuffer)baseAtlas.Framebuffer).OnDispose = () => { Assert.All(receivers, r => Assert.True(r.Disposed)); Assert.True(modelSet.Disposed); baseReleases++; releaseOrder.Add("baseAtlas"); };
            ((FakeFramebuffer)transientAtlas.Framebuffer).OnDispose = () => { Assert.All(receivers, r => Assert.True(r.Disposed)); Assert.True(modelSet.Disposed); transientReleases++; releaseOrder.Add("transientAtlas"); };
            if (failing == "tileChild") ((FakeTexture)receivers[4].Resources[1]).OnDispose = () => { releaseOrder.Add("tileChild"); throw childError; };
            ((FakeFramebuffer)work.Target).OnDispose = () => { releaseOrder.Add("target"); throw targetError; };
            return work;
        };
        rig.SessionCreated = session => OverridePrepare(session, () => throw frameError);
        var outcome = rig.Run();
        _output.WriteLine("receiver={0} actual stages={1} release order={2}", failing, string.Join(",", outcome.Failures.Select(f => f.Stage)), string.Join(",", releaseOrder));
        Assert.Same(frameError, outcome.Failures[0].Error);
        Assert.Equal("renderer.frame", outcome.Failures[0].Stage);
        int eventIndex = 1;
        for (int i = 0; i < refuse.Length; i++)
            if (refuse[i])
            {
                Assert.Same(errors[i], outcome.Failures[eventIndex].Error);
                Assert.Equal("renderer.session.dispose.scene." + kinds[i] + "." + ordinals[i], outcome.Failures[eventIndex++].Stage);
            }
        if (failing == "tileChild")
        {
            Assert.Same(childError, outcome.Failures[eventIndex].Error);
            Assert.Equal("renderer.session.dispose.scene.tileGround.albedo.40", outcome.Failures[eventIndex++].Stage);
        }
        Assert.Same(targetError, outcome.Failures[eventIndex].Error);
        Assert.Equal("renderer.session.dispose.target.2", outcome.Failures[eventIndex++].Stage);
        Assert.Equal(eventIndex, outcome.Failures.Length);
        Assert.Equal("original prepare", outcome.Report!.Publication.OriginalFailure);
        bool retained = refuse.Any(r => r);
        for (int i = 0; i < receivers.Length; i++)
        {
            Assert.Equal(i >= refuse.Length || !refuse[i], receivers[i].Disposed);
            Assert.Equal(1, receivers[i].DisposeCalls);
        }
        Assert.True(((FakeTexture)receivers[5].Resources[1]).Disposed);
        Assert.True(Field<FakeTexture>(work!, "_texture").Disposed);
        Assert.Equal(refuse.Count(r => r), ShadowReceivers(work!.Scene).Cast<FakeResourceSet>().Count(r => !r.Disposed));
        Assert.All(receivers.Where(r => !r.Disposed).SelectMany(r => r.Resources).OfType<FakeTexture>(), t => Assert.False(t.Disposed));
        Assert.True(receivers[5].Disposed); Assert.Contains("laterTile", releaseOrder); Assert.Equal("target", releaseOrder.Last());
        Assert.Equal(retained ? 0 : 1, baseReleases); Assert.Equal(retained ? 0 : 1, transientReleases);
        Assert.Equal(!retained, ((FakeTexture)baseAtlas!.Texture).Disposed);
        Assert.Equal(!retained, ((FakeTexture)transientAtlas!.Texture).Disposed);
        Assert.Equal(retained, ReferenceEquals(baseAtlas.Texture, work!.Scene.PointShadowTexture));
        Assert.Equal(retained, ReferenceEquals(transientAtlas.Texture, work.Scene.PointShadowTransientTexture));
        Assert.Equal(!retained, modelSet!.Disposed); Assert.Equal(!retained, modelDefault!.Disposed);
        Assert.Equal(retained ? 0 : 1, modelUniform!.DisposeCalls);
        Assert.Equal(!refuse.Take(3).Any(r => r), albedo!.Disposed);
        Assert.True(work.Scene.DisposalSafeForDependants);
        int attempts = releaseOrder.Count;
        work.Dispose(); work.Scene.Dispose();
        Assert.Equal(attempts, releaseOrder.Count);
        Assert.All(receivers, r => Assert.Equal(1, r.DisposeCalls));
        Assert.Equal(retained ? 0 : 1, baseReleases); Assert.Equal(retained ? 0 : 1, transientReleases);
        Assert.Equal(0, rig.Diagnostic.CompilerReads);
        _output.WriteLine("receiver={0} boundPairs=6 retained={1} baseReleases={2} transientReleases={3} modelUniformReleases={4} laterTileReleased={5} originalReferenceOrder=true noRetry=true",
            failing, retained, baseReleases, transientReleases, modelUniform.DisposeCalls, receivers[5].Disposed);
    }

    static List<IGpuResourceSet> ShadowReceivers(Scene3D scene)
    {
        var result = new List<IGpuResourceSet>();
        typeof(Scene3D).GetMethod("CollectLiveMaterialSets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, [result]);
        return result;
    }
}
