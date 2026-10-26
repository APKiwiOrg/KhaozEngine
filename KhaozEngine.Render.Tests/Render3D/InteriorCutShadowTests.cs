using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutShadowTests
{
    [Fact]
    public void OptedInCastersRouteWithoutChangingOtherPolicies()
    {
        var mesh = new MeshHandle(0, 1);
        var draws = new[]
        {
            new RigidInstanceDraw(mesh, Matrix4x4.Identity),
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true },
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true, CastsShadows = false, Dissolve = .4f },
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true, Dissolve = .4f },
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true, DissolveComplement = 1f },
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true, Dissolve = .4f, InvertShadowDissolve = true },
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true, ShadowOnly = true },
        };
        ShadowCastKind[] expected = [ShadowCastKind.Opaque, ShadowCastKind.Dissolving, ShadowCastKind.None,
            ShadowCastKind.Dissolving, ShadowCastKind.Dissolving, ShadowCastKind.DissolvingInverted, ShadowCastKind.Dissolving];
        for (int i = 0; i < draws.Length; i++)
        {
            ShadowCastKind kind = Scene3D.ClassifyCaster(new SceneInstances.Instance(draws[i]));
            Assert.Equal(expected[i], kind);
            if (kind == ShadowCastKind.None) continue;
            Assert.Equal(i == 5 ? ShadowDepthVariant.CutoutInverted : ShadowDepthVariant.Cutout,
                ShadowDepthSelection.ForRigidSpan(kind, true));
        }
    }

    [Fact]
    public void InteriorVersionInvalidatesBothCaches()
    {
        using var stage = new Stage();
        stage.Frame();
        stage.Frame(); // the point atlas is allocated at the second frame boundary
        AssertClean(stage.Frame());
        foreach (InteriorHeightField? field in new[] { Field(), Field(1), null, Field() })
        {
            stage.Scene.InteriorCut = field;
            ShadowPassDiagnostics changed = stage.Frame();
            Assert.True(changed.Rendered);
            Assert.True(changed.CasterDataChanged);
            Assert.Equal(1, changed.PointStaticRebuilds);
            AssertClean(stage.Frame());
        }
        // The caster is already Dissolving, so the kind and threshold do not change with this bit.
        stage.Cut = false;
        ShadowPassDiagnostics flagChanged = stage.Frame();
        Assert.True(flagChanged.Rendered);
        Assert.Equal(1, flagChanged.PointStaticRebuilds);
        AssertClean(stage.Frame());
    }

    static void AssertClean(ShadowPassDiagnostics diagnostics)
    {
        Assert.False(diagnostics.Rendered);
        Assert.Equal(0, diagnostics.PointStaticRebuilds);
    }

    [Fact]
    public void SkinnedShadowPairsRemainUncut()
    {
        string[] hashes = ["641265448f5a8fbed2b10a027e8b80f9fea6044d2dd223bc3d95b70c4f4293c5",
            "798d9cb352ccc0653b9d1da24b8a0586f13a2a4f2c14802c6de5c36ecfb00061",
            "09b5e48e5a63e3af140422a6bc3cfccb2bd7c4e8b94706dc9f234ee57d153b31",
            "0f31914686c170254fc8841b55be8a2a2219f437b19e60bdc0044210c9d0d550"];
        int index = 0;
        foreach (var (vertex, name, layouts) in new[]
        {
            (ShaderSources.SkinnedShadowDepthDissolveVert, "SkinnedShadowDepthDissolveFrag", 2),
            (ShaderSources.SkinnedShadowDepthDissolveVert, "SkinnedShadowDepthDissolveInvertedFrag", 2),
            (ShaderSources.PointShadowSkinnedDissolveVert, "PointShadowSkinnedDissolveFrag", 3),
            (ShaderSources.PointShadowSkinnedDissolveVert, "PointShadowSkinnedDissolveInvertedFrag", 3),
        })
        {
            string fragment = Constant(name);
            Assert.DoesNotContain("interiorCut", fragment);
            Assert.Equal(hashes[index++], Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fragment.ReplaceLineEndings("\n")))));
            var pair = SpirvCrossCompile.GlslPairToHlsl(vertex, fragment);
            Assert.Equal(layouts, pair.Reflection.ResourceLayouts.Length);
            Assert.All(pair.Reflection.ResourceLayouts, layout => Assert.Single(layout.Elements));
            ShaderValidation.ValidatePair(vertex, fragment, name);
        }
        using var stage = new Stage();
        stage.Frame();
        stage.Frame();
        var pipelines = ((FakeGpuResourceFactory)stage.Device.Factory).GraphicsPipelines;
        foreach (string name in new[] { "SkinnedShadowDepthDissolveFrag", "SkinnedShadowDepthDissolveInvertedFrag",
            "PointShadowSkinnedDissolveFrag", "PointShadowSkinnedDissolveInvertedFrag" })
            Assert.Contains(pipelines, pipeline => pipeline.FragmentGlsl == Constant(name));
    }

    [Fact]
    public void DirectPointDepthFrameBindsTheBorrowedDefault()
    {
        using var stage = new Stage();
        Assert.True(stage.Scene.EnsurePointShadowAtlas(64, 1));
        stage.Scene.BeginPointShadowFrame(1);
        var factory = (FakeGpuResourceFactory)stage.Device.Factory;
        var texture = factory.ResourceSets.First(set => !set.Disposed && set.Resources.Length == 16).Resources[14];
        Assert.Single(factory.ResourceSets, set => !set.Disposed && set.Resources.Length == 2
            && ReferenceEquals(set.Resources[0], texture));
    }

    [Fact]
    public void ClearingTheFieldRetriesBorrowersAfterAFailedEmptyFrame()
    {
        using var stage = new Stage();
        stage.Scene.InteriorCut = Field();
        stage.Frame();
        stage.Frame();
        var factory = (FakeGpuResourceFactory)stage.Device.Factory;
        var texture = Assert.IsType<FakeTexture>(factory.ResourceSets.First(set => !set.Disposed
            && set.Resources.Length == 16).Resources[14]);
        FakeResourceSet[] borrowers = factory.ResourceSets.Where(set => !set.Disposed
            && set.Resources.Length == 2 && set.Resources.Contains(texture)).ToArray();
        Assert.Equal(4, borrowers.Length);
        texture.OnDispose = () => Assert.All(borrowers, set => Assert.True(set.Disposed));
        int receivers = factory.ResourceSets.Count(set => !set.Disposed
            && set.Resources.Length == 16 && ReferenceEquals(set.Resources[14], texture));
        stage.Scene.InteriorCut = null;
        factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + receivers + 1;
        Assert.Throws<InvalidOperationException>(() => { stage.Frame(draw: false); });
        Assert.False(texture.Disposed);
        factory.ThrowOnResourceSetCreate = 0;

        stage.Frame(draw: false);

        Assert.True(texture.Disposed);
        Assert.Equal(0, factory.LiveSetsNamingAFreedTexture);
    }

    [Fact]
    public void CutBindingsAreSharedAndBorrowersRetireBeforeTheirTexture()
    {
        using var stage = new Stage();
        stage.Scene.InteriorCut = Field();
        stage.Frame();
        stage.Frame();
        var factory = (FakeGpuResourceFactory)stage.Device.Factory;
        FakeTexture Texture() => (FakeTexture)factory.ResourceSets.First(set => !set.Disposed
            && set.Resources.Length == 16).Resources[14];
        FakeResourceSet[] Borrowers(FakeTexture texture) => factory.ResourceSets.Where(set => !set.Disposed
            && set.Resources.Contains(texture)).ToArray();
        FakeTexture first = Texture();
        Assert.Equal(4, Borrowers(first).Count(set => set.Resources.Length == 2));
        int sets = factory.ResourceSets.Count;
        stage.Frame();
        Assert.Equal(sets, factory.ResourceSets.Count);
        stage.Scene.InteriorCut = Field(1);
        stage.Frame();
        Assert.Same(first, Texture());
        Assert.Equal(sets, factory.ResourceSets.Count);
        FakeResourceSet[] firstBorrowers = Borrowers(first);
        first.OnDispose = () => Assert.All(firstBorrowers, set => Assert.True(set.Disposed));
        stage.Scene.InteriorCut = Field(cells: 64);
        stage.Frame();
        Assert.True(first.Disposed);
        Assert.Equal(0, factory.LiveSetsNamingAFreedTexture);
        FakeTexture resized = Texture();
        Assert.Equal(4, Borrowers(resized).Count(set => set.Resources.Length == 2));
        FakeResourceSet[] lastBorrowers = Borrowers(resized);
        resized.OnDispose = () => Assert.All(lastBorrowers, set => Assert.True(set.Disposed));
        stage.Dispose();
        Assert.True(resized.Disposed);
    }

    [Fact]
    public void RigidPairsReflectTheCutWithoutAnotherUniformDescriptor()
    {
        foreach (var (vertex, fragment, set) in RigidPairs())
        {
            Assert.Contains(ShaderSources.InteriorCutGlsl, fragment);
            Assert.Contains("if (vCut.w > 0.5 && interiorCut(vCut.xyz)) discard;", fragment);
            var pair = SpirvCrossCompile.GlslPairToHlsl(vertex, fragment);
            var layouts = pair.Reflection.ResourceLayouts;
            Assert.Equal(set + 1, layouts.Length);
            var buffer = Assert.Single(layouts[0].Elements);
            Assert.Equal(GpuResourceKind.UniformBuffer, buffer.Kind);
            Assert.Equal(GpuShaderStages.Vertex | GpuShaderStages.Fragment, buffer.Stages);
            Assert.Equal(new[] { GpuResourceKind.TextureReadOnly, GpuResourceKind.Sampler },
                layouts[set].Elements.Select(element => element.Kind));
            Assert.All(layouts[set].Elements, element => Assert.Equal(GpuShaderStages.Fragment, element.Stages));
            ShaderValidation.ValidatePair(vertex, fragment, "InteriorShadow");
        }
    }

    internal static (string Vertex, string Fragment, int Set)[] RigidPairs() =>
    [
        (ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag, 1),
        (ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveInvertedFrag, 1),
        (ShaderSources.ShadowDepthCutoutVert, ShaderSources.ShadowDepthCutoutFrag, 2),
        (ShaderSources.ShadowDepthCutoutVert, ShaderSources.ShadowDepthCutoutInvertedFrag, 2),
        (ShaderSources.PointShadowRigidDissolveVert, ShaderSources.PointShadowRigidDissolveFrag, 1),
        (ShaderSources.PointShadowRigidDissolveVert, ShaderSources.PointShadowRigidDissolveInvertedFrag, 1),
    ];

    internal static string Constant(string name) => (string)Assert.IsAssignableFrom<FieldInfo>(
        typeof(ShaderSources).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)).GetRawConstantValue()!;

    internal static InteriorHeightField Field(float minimum = -2, int cells = 32) => InteriorHeightFieldBuilder.Build([new InteriorVolume
    {
        Outline = [new(0, -2), new(2, -2), new(2, 2), new(0, 2)], FloorY = -1, CeilingY = 3,
    }], new InteriorWindow(new Vector2(minimum), .25f, cells), InteriorEncoding.For(-1, 3));

    sealed class Stage : IDisposable
    {
        internal FakeGpuDevice Device { get; } = new();
        readonly IGpuTexture _texture;
        readonly IGpuFramebuffer _target;
        readonly MeshHandle _box;
        internal Scene3D Scene { get; }
        internal bool Cut { get; set; } = true;
        bool _disposed;

        internal Stage()
        {
            _texture = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                128, 96, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            _target = Device.Factory.CreateFramebuffer(null, _texture);
            Scene = new Scene3D(Device, _target.Outputs);
            Scene.Post.Starfield = false;
            Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            Scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
            Scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 1;
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(6));
            _box = Scene.LoadMesh(MeshPrimitives.Box(1));
        }

        internal ShadowPassDiagnostics Frame(bool draw = true)
        {
            using var commands = Device.Factory.CreateCommandList();
            Scene.Begin();
            if (draw)
            {
                Scene.Draw(new RigidInstanceDraw(_box, Matrix4x4.Identity) { CutInsideInteriors = Cut, Dissolve = .4f });
                Scene.AddLight(new Vector3(0, 4, 0), Color.White, 10, 1, LightShadow.Static(1));
            }
            Scene.PrepareFrame();
            commands.Begin();
            Scene.RenderInternal(commands, 128, 96, _target);
            commands.End();
            return Scene.LastShadowPassDiagnostics;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Scene.Dispose();
            _target.Dispose();
            _texture.Dispose();
            Device.Dispose();
        }
    }
}
