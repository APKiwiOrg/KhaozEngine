using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class FoliageCutPixelIdentityGpuTests
{
    const int Width = 128, Height = 96;

    public static IEnumerable<object[]> Cases()
    {
        for (int kind = 0; kind < 4; kind++) yield return [kind, null!];
        // Private consumer exports can join the same exact oracle without becoming public engine assets.
        string? root = Environment.GetEnvironmentVariable("KE_FOLIAGE_IDENTITY_MODELS");
        if (root is not null)
            foreach (string file in Directory.GetFiles(root, "*.glb").Order(StringComparer.Ordinal))
                yield return [0, file];
    }

    [GpuTheory]
    [MemberData(nameof(Cases))]
    public void OpaqueMaterialCutPreservesAttachmentsAndHistory(int materialKind, string? model)
    {
        using var context = GpuDeviceContext.CreateHeadless();
        int restored = 0;
        GpuResourceLayoutDescription? genericLayout = null;
        IGpuBindableResource[]? defaults = null;
        using var baseline = new ShaderOverrideGpuDevice(context.GpuDevice, source =>
        {
            if (!source.Contains("// Opaque no-map foliage material.", StringComparison.Ordinal)) return source;
            restored++;
            bool motion = source.Contains("out vec4 oMotion;", StringComparison.Ordinal);
            string generic = motion ? ShaderSources.ModelMotionFrag : ShaderSources.ModelFrag;
            return source.Contains("out float oKeyVisibility;", StringComparison.Ordinal)
                ? ShadowVisibilityTargets.Fragment(generic, motion) : generic;
        }, layout =>
        {
            if (layout.Elements.Length == 16 && layout.Elements[0].Name == "U") genericLayout = layout;
            return layout.Elements.Length == 11 && layout.Elements[3].Name == "ShadowMap"
                ? genericLayout!.Value : layout;
        }, set =>
        {
            if (set.Resources.Length == 16) defaults = set.Resources;
            return set.Resources.Length == 11 && defaults is not null
                ? new GpuResourceSetDescription(set.Layout,
                    [.. set.Resources[..3], .. defaults[3..7], .. set.Resources[3..10], defaults[14], set.Resources[10]])
                : set;
        });
        byte[][] expected = Render(baseline, materialKind: materialKind, model: model);
        byte[][] actual = Render(context.GpuDevice, materialKind: materialKind, model: model);
        byte[][] control = Render(context.GpuDevice, changeColor: true, materialKind: materialKind, model: model);
        Assert.Equal(materialKind == 0, restored > 0);
        Assert.Equal(4 * 10, expected.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
        Assert.False(actual[0].SequenceEqual(control[0]), "Foliage must contribute HDR pixels.");
    }

    internal static byte[][] Render(IGpuDevice device, bool changeColor = false, int materialKind = 0, string? model = null)
    {
        using var display = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = device.Factory.CreateFramebuffer(null, display);
        using var scene = new Scene3D(device, target.Outputs,
            new ShadowSettings { ShadowMapResolution = 256, ShadowCascadeCount = 2 }, requestLitVisibility: true);
        scene.ForceTemporalForTests = true;
        scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Hdr.Enabled = true;
        scene.Post.Starfield = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.Background = BackgroundMode.Solid;
        scene.Post.Outline = false;
        scene.Post.CelBands = 0;
        scene.Post.AmbientColor = new Color(.3f, .3f, .3f, 1f);
        scene.Post.FillLightColor = Color.Black;
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(.3f, -1f, .4f));
        scene.Post.LightColor = Color.White;
        scene.Camera.Azimuth = .3f;
        scene.Camera.Elevation = .95f;
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.OrthoSize = 6f;
        GltfMesh[] meshes = model is null ? [FoliageShadowGpuScene.ShrubMesh()]
            : GltfLoader.LoadPartsWithMaterials(model).Select(part => part.Mesh).ToArray();
        Assert.NotEmpty(meshes);
        var instances = new List<FoliageInstance>();
        foreach (GltfMesh mesh in meshes)
        {
            foreach (ref ModelVertex vertex in mesh.Vertices.AsSpan())
            {
                if (model is null) vertex.Tangent = materialKind == 3 ? new Vector4(1, 0, 0, 1) : Vector4.Zero;
                else Assert.Equal(Vector4.Zero, vertex.Tangent);
                vertex.Color *= changeColor ? .5f : 1f;
            }
            Scene3D.TextureHandle albedo = materialKind is 1 or 2
                ? scene.LoadTexture([90, 150, 30, 255], 1, 1) : default;
            MeshHandle shrub = scene.LoadMesh(mesh, new Scene3D.SurfaceMaps(albedo,
                alphaCutoff: materialKind == 2 ? .5f : 0f));
            instances.Add(new(shrub, Matrix4x4.CreateTranslation(-1f, 0f, 0f), .1f,
                new FoliageMotionProfile(.3f, .15f, .4f, .035f), CastsShadows: true));
            instances.Add(new(shrub, Matrix4x4.CreateTranslation(1f, 0f, 0f), .8f));
        }
        using FoliageBatch batch = scene.CreateFoliageBatch(instances.ToArray());
        using var commands = device.Factory.CreateCommandList();
        var frames = new List<byte[]>();
        for (int frame = 0; frame < 4; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = frame * .17f;
            scene.Camera.Target = new Vector3(frame * .03f, .5f, 0f);
            scene.RenderOrigin = frame >= 2 ? new Vector3(1024, 0, 1024) : Vector3.Zero;
            scene.Post.Quality.Shadows.Mode = frame % 2 == 0 ? ShadowMode.Off : ShadowMode.ShadowMap;
            scene.Post.Atmosphere.Enabled = frame >= 2;
            var settings = new FoliageRenderSettings
            {
                DrawRadius = 10, DistantDensity = 1, WindStrength = .2f,
                WindFadeBladePixels = 3,
            };
            scene.DrawFoliage(batch, Vector3.Zero, settings,
                [new FoliageInteractor(new Vector3(-.8f + .1f * frame, 0, 0), 1.5f, .8f, Agitation: 1f)]);
            scene.PrepareFrame(Width, Height);
            using (GpuRecording.Open(device, commands, nameof(FoliageCutPixelIdentityGpuTests)))
                scene.RenderInternal(commands, Width, Height, target);
            device.Submit(commands);
            device.WaitForIdle();
            TemporalHistory history = scene.TemporalHistory;
            frames.AddRange(new[]
            {
                scene.TemporalPostTargetsForTests!.OpaqueColor, scene.ResolvedNormalTarget, scene.ResolvedDepthTarget,
                scene.MotionTargetForTests!, scene.ResolvedVisibilityTarget!, display,
                history.Color(history.WriteIndex), history.Confidence(history.WriteIndex),
                history.PreviousDepth(history.WriteIndex),
            }.Select(texture => PointShadowZeroNdlGpuTests.Read(device, texture)));
            float[] shadow = scene.DebugReadShadowMap(out _, out _);
            frames.Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(shadow.AsSpan()).ToArray());
        }
        Assert.Contains(Enumerable.Range(0, Width * Height), pixel => BitConverter.ToSingle(frames[2], pixel * 4) < 1f);
        return frames.ToArray();
    }
}
