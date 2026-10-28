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
    bool _cold, _validated, _disposed;
    long _frames;
    int _firstIndex;

    internal RendererPerfSceneWorkload(IGpuDevice device, IRenderPassObserver observer, RendererPerfRecipeIdentity expected,
        Action<IGpuDevice, IGpuTexture>? validate = null)
    {
        _device = device; _observer = observer; _identity = expected; _validate = validate;
        RendererPerfRecipe? recipe = null;
        IGpuTexture? texture = null;
        IGpuFramebuffer? target = null;
        Scene3D? scene = null;
        try
        {
            _recipe = recipe = RendererPerfRecipe.Create(RendererPerfRecipe.IdFromVersion(expected.Version));
            var facts = _recipe.Facts;
            if (expected.Version != facts.Id + "/" + facts.Version || expected.Profile != facts.Profile
                || expected.RecipeSha256 != facts.RecipeSha256 || expected.SettingsSha256 != facts.SettingsSha256
                || expected.GeometrySha256 != facts.GeometrySha256 || expected.AssetSha256 != facts.AssetSha256)
                throw new RendererPerfProtocolException("Identity", "The native recipe differs from the authorized construction.");
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
        catch (Exception error)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            RendererPerfFailures.Add(failures, "renderer.scene.construct", error);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.scene", scene is null ? null : scene.Dispose);
            if (scene is null || scene.DisposalSafeForDependants)
            {
                RendererPerfFailures.Release(failures, "renderer.session.dispose.target.0", target is null ? null : target.Dispose);
                RendererPerfFailures.Release(failures, "renderer.session.dispose.texture.1", texture is null ? null : texture.Dispose);
                RendererPerfFailures.Release(failures, "renderer.session.dispose.recipe.2", recipe is null ? null : recipe.Dispose);
            }
            throw new GpuDiagnosticFailureException(failures.ToImmutable());
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
        // The first counted frame owns scene frame one, motion, TAA history and caches.
        // Conditioning starts at zero. Scene blocks start at their negative warm-up index.
        _recipe.PrepareCold(Scene, _device, firstFrameIndex);
        _recipe.StartBlockClock(firstFrameIndex);
        Scene.PassObserver = _observer;
        _cold = true;
    }

    public void Prepare(int frameIndex) => Prepare(frameIndex, null);

    public void Prepare(int frameIndex, GpuFrameSubmission? submission)
    {
        if (submission is null) Scene.Begin(); else Scene.BeginFrame(submission);
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
        // Only conditioning starts a fresh scene at zero. Measured zero follows counted warm-up.
        if (_recipe.Facts.Schedule.Temporal && !Scene.TemporalHistory.IsValid
            && !(_firstIndex == 0 && frameIndex == 0 && _frames == 1 && Scene.TemporalActive
                && Scene.TemporalHistory.LastReset == TemporalResetReason.FirstFrame))
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
        if (_disposed) return;
        _disposed = true;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        RendererPerfFailures.Release(failures, "renderer.session.dispose.ui.0", _ui is null ? null : _ui.Dispose);
        // SpriteBatch and Scene3D each own a prerequisite drain. A failed prerequisite retains dependants.
        if (_ui is null || _ui.DisposalSafeForDependants)
        {
            RendererPerfFailures.Release(failures, "renderer.session.dispose.scene.1", Scene.Dispose);
            if (Scene.DisposalSafeForDependants)
            {
                RendererPerfFailures.Release(failures, "renderer.session.dispose.target.2", Target.Dispose);
                RendererPerfFailures.Release(failures, "renderer.session.dispose.texture.3", _texture.Dispose);
                RendererPerfFailures.Release(failures, "renderer.session.dispose.recipe.4", _recipe.Dispose);
            }
        }
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
}
