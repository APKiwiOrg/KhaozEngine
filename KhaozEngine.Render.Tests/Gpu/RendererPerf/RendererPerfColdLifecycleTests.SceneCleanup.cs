using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_ui_child_failure_continues_safe_scene_and_attachments_but_failed_drain_retains_them(bool failDrain)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var operational = new InvalidOperationException("stop before scene recording");
        var uiError = new InvalidOperationException("UI uniform release");
        var uiPipeline = new InvalidOperationException("UI pipeline release");
        var sceneError = new InvalidOperationException("scene texture release");
        var targetError = new InvalidOperationException("attachment framebuffer release");
        var attachmentError = new InvalidOperationException("attachment texture release");
        var drain = new InvalidOperationException("UI drain");
        var observed = new List<string>();
        RendererPerfSceneWorkload? work = null;
        SubmissionTestBuffer? uniform = null;
        FakeTexture? sceneTexture = null;
        FakeFramebuffer? target = null;
        int drains = 0, pipelines = 0, recipeReleases = 0;
        bool failing = false;
        using var recipe = RendererPerfRecipe.Create("effects-ui");
        var identity = CleanupIdentity(recipe);
        rig.WorkFactory = (device, _, observer) =>
        {
            work = new RendererPerfSceneWorkload(device, observer, identity);
            var ui = Field<SpriteBatch>(work, "_ui");
            uniform = Field<SubmissionTestBuffer>(ui, "_vpUbo");
            uniform.OnDispose = () => { observed.Add("ui.uniform"); throw uiError; };
            work.Scene.LoadTexture(new byte[4], 1, 1);
            sceneTexture = Field<List<IGpuTexture>>(work.Scene, "_textures").Last() as FakeTexture;
            sceneTexture!.OnDispose = () => { observed.Add("scene.texture"); throw sceneError; };
            target = (FakeFramebuffer)work.Target;
            target.OnDispose = () => { observed.Add("target"); throw targetError; };
            Field<FakeTexture>(work, "_texture").OnDispose = () => { observed.Add("attachment"); throw attachmentError; };
            var actualRecipe = Field<RendererPerfRecipe>(work, "_recipe");
            foreach (var texture in Field<Texture2D[]>(actualRecipe.UiScene!, "_textures"))
                ((FakeTexture)texture.Handle).OnDispose = () => { observed.Add("recipe.texture"); recipeReleases++; };
            return work;
        };
        rig.SessionCreated = session => OverridePrepare(session, () => { failing = true; throw operational; });
        rig.Boundary = stage =>
        {
            if (failing && stage == "production.drain") { drains++; if (failDrain && drains == 3) throw drain; }
            if (failing && stage == "production.dispose.pipeline" && ++pipelines == 1) { observed.Add("ui.pipeline"); throw uiPipeline; }
        };
        var outcome = rig.Run();
        _output.WriteLine("UI actual events={0}; releases={1}", string.Join("|", outcome.Failures.Select(f => f.Stage + ":" + f.Error)), string.Join(",", observed));
        Assert.Same(operational, outcome.Failures[0].Error);
        Exception[] expected = failDrain ? [operational, drain] : [operational, uiError, uiPipeline, sceneError, targetError, attachmentError];
        Assert.Equal(expected.Length, outcome.Failures.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], outcome.Failures[i].Error);
        Assert.Equal(failDrain ? new[] { "renderer.frame", "renderer.session.dispose.ui.drain.0" } : new[]
        {
            "renderer.frame", "renderer.session.dispose.ui.viewUniform.3", "renderer.session.dispose.ui.pipeline.5",
            "renderer.session.dispose.scene.texture.172", "renderer.session.dispose.target.2", "renderer.session.dispose.texture.3"
        }, outcome.Failures.Select(f => f.Stage));
        if (failDrain)
        {
            Assert.Empty(observed); Assert.Equal(0, uniform!.DisposeCalls);
            Assert.False(work!.Scene.DisposalSafeForDependants); Assert.False(target!.Disposed);
            Assert.False(sceneTexture!.Disposed); Assert.Equal(0, recipeReleases);
        }
        else
        {
            Assert.Equal(new[] { "ui.uniform", "ui.pipeline", "scene.texture", "target", "attachment", "recipe.texture", "recipe.texture", "recipe.texture" }, observed);
            Assert.True(work!.Scene.DisposalSafeForDependants); Assert.Equal(1, uniform!.DisposeCalls);
            Assert.True(pipelines > 2); Assert.Equal(3, recipeReleases);
        }
        int releases = observed.Count;
        work!.Dispose(); Assert.Equal(releases, observed.Count);
        Assert.Equal(0, rig.Diagnostic.CompilerReads);
        _output.WriteLine("UI failedDrain={0} drains={1} safe release order={2} actual stages={3}", failDrain, drains,
            string.Join(",", observed), string.Join(",", outcome.Failures.Select(f => f.Stage)));
    }

    [Fact]
    public void Actual_scene_grouped_foliage_and_temporal_tails_reach_unit_in_release_order()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var operational = new InvalidOperationException("stop before record");
        var first = new InvalidOperationException("foliage first"); var second = new InvalidOperationException("foliage second");
        var temporal = new InvalidOperationException("temporal target first"); var temporalTail = new InvalidOperationException("temporal later independent depth");
        var observed = new List<string>();
        FoliageBatch? a = null, b = null;
        SubmissionTestBuffer? av = null, bv = null;
        FakeTexture? depthTail = null, postTail = null;
        RendererPerfSceneWorkload? work = null;
        using var recipe = RendererPerfRecipe.Create("empty");
        rig.WorkFactory = (device, _, observer) =>
        {
            work = new RendererPerfSceneWorkload(device, observer, CleanupIdentity(recipe));
            var scene = work.Scene;
            var mesh = scene.LoadMesh(MeshPrimitives.Box(1));
            a = scene.CreateFoliageBatch([new(mesh, Matrix4x4.Identity, .25f)]);
            b = scene.CreateFoliageBatch([new(mesh, Matrix4x4.Identity, .75f)]);
            using var commands = device.Factory.CreateCommandList();
            a.UploadPending(device, commands); b.UploadPending(device, commands);
            av = (SubmissionTestBuffer)a.Buffer!; bv = (SubmissionTestBuffer)b.Buffer!;
            av.OnDispose = () => { observed.Add("foliage.0"); throw first; };
            bv.OnDispose = () => { observed.Add("foliage.1"); throw second; };
            var res = Field<RenderResources>(scene, "_res");
            scene.TemporalHistory.EnsureTargets(device, 8, 8, 8, 8);
            var post = new TemporalPostTargets(device);
            post.Ensure(res, scene.TemporalHistory, 8, 8, false);
            typeof(Scene3D).GetField("_temporalPost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scene, post);
            postTail = Field<FakeTexture>(post, "_pingB");
            Field<FakeTexture>(post, "_pingA").OnDispose = () => { observed.Add("temporal.post"); throw temporal; };
            depthTail = (FakeTexture)scene.TemporalHistory.PreviousDepth(1);
            depthTail.OnDispose = () => { observed.Add("temporal.history"); throw temporalTail; };
            return work;
        };
        rig.SessionCreated = session => OverridePrepare(session, () => throw operational);
        var outcome = rig.Run();
        _output.WriteLine("group actual events={0}; releases={1}", string.Join("|", outcome.Failures.Select(f => f.Stage + ":" + f.Error)), string.Join(",", observed));
        Assert.Same(operational, outcome.Failures[0].Error);
        Exception[] expected = [operational, first, second, temporal, temporalTail];
        Assert.Equal(expected.Length, outcome.Failures.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], outcome.Failures[i].Error);
        Assert.Equal(new[] { "foliage.0", "foliage.1", "temporal.post", "temporal.history" }, observed);
        Assert.Equal(new[] { "renderer.frame", "renderer.session.dispose.scene.foliage.buffer.3", "renderer.session.dispose.scene.foliage.buffer.4",
            "renderer.session.dispose.scene.temporal.post.pingA.4", "renderer.session.dispose.scene.temporal.history.39" }, outcome.Failures.Select(f => f.Stage));
        Assert.Equal(1, av!.DisposeCalls); Assert.Equal(1, bv!.DisposeCalls);
        Assert.True(a!.IsDisposed); Assert.True(b!.IsDisposed); Assert.Null(a.Buffer); Assert.Null(b.Buffer);
        Assert.True(work!.Scene.DisposalSafeForDependants);
        Assert.True(postTail!.Disposed);
        Assert.True(((FakeFramebuffer)work.Target).Disposed);
        work.Dispose(); Assert.Equal(1, av.DisposeCalls); Assert.Equal(1, bv.DisposeCalls);
        _output.WriteLine("grouped actual release order={0}; stages={1}", string.Join(",", observed), string.Join(",", outcome.Failures.Select(f => f.Stage)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_ui_safety_is_not_stale_after_failed_prerequisite_or_repeated_completed_disposal(bool failDrain)
    {
        using var device = new RendererPerfConstructionFixture();
        var ui = new SpriteBatch(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        var buffer = Field<SubmissionTestBuffer>(ui, "_vpUbo");
        var drain = new InvalidOperationException("prerequisite"); var child = new InvalidOperationException("UI child");
        buffer.OnDispose = () => throw child;
        device.Trace = stage => { if (failDrain && stage == "drain") throw drain; };
        var first = Assert.Throws<GpuDiagnosticFailureException>(ui.Dispose);
        Assert.Same(failDrain ? drain : child, Assert.Single(first.Failures).Error);
        Assert.Equal(!failDrain, ui.DisposalSafeForDependants);
        Assert.Equal(failDrain ? 0 : 1, buffer.DisposeCalls);
        device.Trace = null;
        if (failDrain)
        {
            var retry = Assert.Throws<GpuDiagnosticFailureException>(ui.Dispose);
            Assert.Same(child, Assert.Single(retry.Failures).Error);
        }
        Assert.True(ui.DisposalSafeForDependants);
        int waits = device.Waits;
        ui.Dispose();
        Assert.Equal(1, buffer.DisposeCalls); Assert.Equal(waits, device.Waits);
    }

    static RendererPerfRecipeIdentity CleanupIdentity(RendererPerfRecipe recipe)
    {
        var f = recipe.Facts;
        return new("S0", f.Id + "/" + f.Version, f.Profile, f.RecipeSha256, f.SettingsSha256,
            new string('b', 64), f.GeometrySha256, f.AssetSha256);
    }
    // Fault the actual frame callback before recipe work, keeping owned resources in their constructed state.
    static void OverridePrepare(RendererPerfNativeSession session, Action prepare) =>
        typeof(RendererPerfNativeSession).GetField("_prepare", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, (Action<Frame>)(_ => prepare()));
    static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
