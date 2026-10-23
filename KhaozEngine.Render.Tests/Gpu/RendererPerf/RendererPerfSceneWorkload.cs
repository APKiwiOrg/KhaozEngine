using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed class RendererPerfSceneWorkload : IRendererPerfNativeWork
{
    readonly IGpuDevice _device;
    readonly RendererPerfRecipe _recipe;
    readonly RendererPerfRecipeIdentity _identity;
    readonly IRenderPassObserver _observer;
    readonly IGpuTexture _texture;
    readonly SpriteBatch? _ui;
    readonly Action<IGpuDevice, IGpuTexture>? _validate;
    bool _cold, _validated;
    long _frames;
    int _firstIndex;

    internal RendererPerfSceneWorkload(IGpuDevice device, IRenderPassObserver observer, RendererPerfRecipeIdentity expected,
        Action<IGpuDevice, IGpuTexture>? validate = null)
    {
        _device = device; _observer = observer; _identity = expected; _validate = validate;
        _recipe = RendererPerfRecipe.Create(expected.Version.Split('/')[0]);
        var facts = _recipe.Facts;
        if (expected.Version != facts.Id + "/" + facts.Version || expected.Profile != facts.Profile
            || expected.RecipeSha256 != facts.RecipeSha256 || expected.SettingsSha256 != facts.SettingsSha256
            || expected.GeometrySha256 != facts.GeometrySha256 || expected.AssetSha256 != facts.AssetSha256)
        {
            _recipe.Dispose();
            throw new RendererPerfProtocolException("Identity", "The native recipe differs from the authorized construction.");
        }
        IGpuTexture? texture = null;
        IGpuFramebuffer? target = null;
        Scene3D? scene = null;
        try
        {
            _texture = texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(RendererPerfRecipe.Width,
                RendererPerfRecipe.Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Target = target = device.Factory.CreateFramebuffer(null, texture);
            Scene = scene = new(device, target.Outputs);
            _recipe.ResetBlock(scene);
            if (_recipe.UiScene is { } ui)
            {
                ui.Load(device);
                _ui = new SpriteBatch(device, target.Outputs);
            }
        }
        catch
        {
            scene?.Dispose(); target?.Dispose(); texture?.Dispose(); _recipe.Dispose();
            throw;
        }
    }

    internal Scene3D Scene { get; }
    public IGpuFramebuffer Target { get; }
    public Color ClearColor => Scene.Post.BackgroundColor;
    public RendererPerfRecipeReport Recipe
    {
        get
        {
            var expected = ImmutableDictionary<string, long>.Empty
                .Add("actors.visible", _recipe.Facts.ActorInstances)
                .Add("actors.culled", 0).Add("directional.cascades", _recipe.Facts.Schedule.DirectionalCascades);
            var actual = ImmutableDictionary<string, long>.Empty
                .Add("actors.visible", Scene.DrawnSkinnedInstances).Add("actors.culled", Scene.CulledSkinnedInstances)
                .Add("directional.cascades", Scene.LastShadowPassDiagnostics.CascadeCount)
                .Add("rigid.visible", Scene.DrawnInstances).Add("rigid.culled", Scene.CulledInstances)
                .Add("foliage.submittedPatches", Scene.LastFoliageStats.SubmittedPatches)
                .Add("foliage.candidateInstances", Scene.LastFoliageStats.CandidateInstances);
            var facts = ImmutableDictionary<string, RendererPerfMetric>.Empty
                .Add("recordedFrames", RendererPerfDriver.Value(_frames, "count", "production scene records, excludes cold preparation"))
                .Add("validation.image", _validated ? RendererPerfDriver.Value(1, "count", "one cold output validation after the final batch")
                    : new(null, "count", "cold output validation", RendererPerfMetricStatus.NotExecuted, "The final batch has not completed."));
            return new(_identity, "public-engine-primitives/1", _recipe.Facts.SequenceSha256, expected, actual, facts);
        }
    }

    public void PrepareCold(int firstFrameIndex)
    {
        if (_cold) throw new InvalidOperationException("Cold preparation runs once per fresh scene.");
        _firstIndex = firstFrameIndex;
        // Pair cold ocean preparation with its compute recording, without Begin or scene rendering.
        // The first counted warm frame still owns frame index one, motion, TAA history and caches.
        _recipe.PrepareCold(Scene, _device, firstFrameIndex);
        _recipe.StartBlockClock(firstFrameIndex);
        Scene.PassObserver = _observer;
        _cold = true;
    }

    public void Prepare(int frameIndex)
    {
        Scene.Begin();
        _recipe.Queue(Scene, frameIndex);
        Scene.PrepareFrame();
    }

    public void Record(IGpuCommandList commands, int frameIndex)
    {
        if (!_cold || frameIndex != _firstIndex + _frames) throw new InvalidOperationException("Scene records must follow the fixed integer sequence.");
        Scene.RenderInternal(commands, RendererPerfRecipe.Width, RendererPerfRecipe.Height, Target);
        if (_ui is { } ui)
        {
            var key = new RenderPassKey("ui.sprites");
            _observer.BeginPass(commands, key);
            try
            {
                commands.SetFramebuffer(Target);
                ui.NewFrame(commands, RendererPerfRecipe.Width, RendererPerfRecipe.Height);
                ui.Begin(); _recipe.UiScene!.Queue(ui, frameIndex); ui.End();
            }
            finally { _observer.EndPass(commands, key); }
        }
        _frames++;
        if (frameIndex < 0) return;
        if (Scene.DrawnSkinnedInstances != _recipe.Facts.ActorInstances || Scene.CulledSkinnedInstances != 0
            || !Scene.UseGpuSkinning || Scene.LastShadowPassDiagnostics.CascadeCount != _recipe.Facts.Schedule.DirectionalCascades)
            throw new InvalidOperationException("A native actor or shadow route differs from the frozen recipe.");
        if (_recipe.Facts.Schedule.Temporal && !Scene.TemporalHistory.IsValid)
            throw new InvalidOperationException("The measured temporal history is invalid.");
        if (_recipe.Facts.Schedule.Features?.WaterPlanes > 0 && Scene.LastWaterStats.OceanStalls != 0)
            throw new InvalidOperationException("The measured ocean unexpectedly primed again.");
    }

    public void ValidateOutput()
    {
        if (_validated) throw new InvalidOperationException("Readback validation is performed once.");
        if (_validate is { } validate) validate(_device, _texture);
        else
        {
            byte[] pixels = GpuReadback.ToRgba(_device, _texture, RendererPerfRecipe.Width, RendererPerfRecipe.Height);
            if (pixels.Length != RendererPerfRecipe.Width * RendererPerfRecipe.Height * 4)
                throw new InvalidOperationException("The final validation image is incomplete.");
        }
        _validated = true;
    }

    public void Dispose()
    {
        _ui?.Dispose(); Scene.Dispose(); Target.Dispose(); _texture.Dispose(); _recipe.Dispose();
    }
}
