using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Grounds Vulkan's flat vertex-location derivation in the GLSL and pipeline descriptions the renderers
    /// actually ship. The factory captures each real renderer request, so neither side repeats its layouts in a
    /// handwritten inventory.
    /// </summary>
    public sealed class VulkanShippedVertexLayoutTests
    {
        static readonly Regex VertexInput = new(
            @"layout\s*\(\s*location\s*=\s*(\d+)[^)]*\)\s*in\s+(float|vec2|vec3|vec4)\s+(\w+)",
            RegexOptions.CultureInvariant);

        [Fact]
        public void ShippedVertexDeclarationsMatchActualRendererPipelineLayouts()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
            using var scene = new Scene3D(device, framebuffer.Outputs);
            using var sprites = new SpriteBatch(device, framebuffer.Outputs);
            using var model = new ModelRenderer(device, framebuffer.Outputs, 64, 1);
            using IGpuCommandList commands = factory.CreateCommandList();
            model.UploadFoliageUniforms(commands, [default]);
            CaptureClipmapPipeline(device, framebuffer.Outputs, commands);
            CaptureBoundedWaterPipeline(scene);
            CaptureTriangleOpticsPipelines(device, framebuffer.Outputs);
            // The point-shadow pass is allocated lazily, so a scene that is never asked for one builds none of its
            // four pipelines and the three caster programs would read here as uncaptured.
            Assert.True(scene.EnsurePointShadowAtlas(PointShadowAtlas.MinFaceResolution, 1));
            CaptureTargetOutlinePipelines(scene, framebuffer, commands);
            CaptureTemporalPipelines(device, commands);
            CaptureTemporalPipelines(device, commands, KhaozEngine.Tests.Render3D.TemporalVisibilityLayoutTests.Layout(false, true));
            CaptureTemporalPipelines(device, commands, KhaozEngine.Tests.Render3D.TemporalVisibilityLayoutTests.Layout(true, true));
            CaptureFoliageShadowPipelines(scene, framebuffer, commands);
            CapturePreparedFoliagePipelines(device);

            ShippedGraphicsProgram[] expectedPrograms = ShippedShaderPrograms.GraphicsPrograms()
                .Where(program => Parse(program.VertexGlsl).Count > 0)
                .ToArray();
            var namesByPair = expectedPrograms.ToDictionary(
                program => PairOf(program), program => program.Name);
            var problems = new List<string>();

            foreach (FakeGraphicsPipelineRequest request in factory.GraphicsPipelines)
            {
                IReadOnlyList<VertexDeclaration> declared = Parse(request.VertexGlsl);
                if (declared.Count == 0) continue;

                ShaderPair pair = PairOf(request);
                VulkanVertexInput.Build(request.Description.VertexLayouts,
                    out VulkanVertexAttribute[] attributes);
                var byLocation = attributes.ToDictionary(attribute => attribute.Location);
                string program = namesByPair.TryGetValue(pair, out string? name)
                    ? name
                    : "unknown shipped pipeline";

                foreach (VertexDeclaration expected in declared)
                {
                    if (!byLocation.TryGetValue(expected.Location, out VulkanVertexAttribute actual))
                    {
                        problems.Add($"{program}: GLSL {expected.Name} at location {expected.Location} has no "
                            + "attribute in the actual pipeline layout.");
                    }
                    else if (expected.Format != actual.Format)
                    {
                        problems.Add($"{program}: GLSL {expected.Name} is location {expected.Location} "
                            + $"{expected.Format}, but the actual pipeline attribute is {actual.Format} from "
                            + $"binding {actual.Binding} offset {actual.Offset}.");
                    }
                }
            }

            foreach (string missing in MissingPrograms(expectedPrograms, factory.GraphicsPipelines))
                problems.Add($"{missing}: no actual renderer pipeline was captured.");

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void R8aResourceCatalogMatchesActualRendererPipelineLayouts()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
            using var scene = new Scene3D(device, framebuffer.Outputs);
            CaptureBoundedWaterPipeline(scene);
            using IGpuCommandList commands = factory.CreateCommandList();
            CaptureTemporalPipelines(device, commands);
            CaptureTemporalPipelines(device, commands, KhaozEngine.Tests.Render3D.TemporalVisibilityLayoutTests.Layout(false, true));
            CapturePreparedFoliagePipelines(device);

            string[] programs = ["BoundedWater", "PreparedFoliage", "PreparedFoliageMotion",
                "PreparedFoliageDepth", "PreparedFoliageDepthCutout", "PreparedFoliagePoint",
                "PreparedFoliageVisibility", "PreparedFoliageMotionVisibility"];
            var catalog = ShippedShaderPrograms.GraphicsPrograms().ToDictionary(program => program.Name);
            foreach (string name in programs)
            {
                var problems = new List<string>();
                string[] slots = VulkanShaderBindingTableTests.SlotsOf(name, problems);
                Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
                var program = catalog[name];
                var captured = factory.GraphicsPipelines.Where(request => PairOf(request) == PairOf(program)).ToArray();
                Assert.NotEmpty(captured);
                foreach (var request in captured)
                {
                    Assert.Equal(slots.Length, request.Description.ResourceLayouts.Length);
                    for (int slot = 0; slot < slots.Length; slot++)
                    {
                        var actual = Assert.IsType<FakeResourceLayout>(request.Description.ResourceLayouts[slot]);
                        Assert.Equal(VulkanDescriptorLimitTests.ShippedLayouts[slots[slot]].Elements,
                            actual.Description.Elements);
                    }
                }
            }
        }

        [Fact]
        public void PreparedFoliageShadowLayoutsMatchMetalReflectionIncludingTheOpaqueGap()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            CapturePreparedFoliagePipelines(device);
            var programs = ShippedShaderPrograms.GraphicsPrograms()
                .Where(p => p.Name is "PreparedFoliageDepth" or "PreparedFoliageDepthCutout").ToArray();
            Assert.Equal(2, programs.Length); // Both shipped cascade variants must be exercised.
            foreach (var program in programs)
            {
                var captured = factory.GraphicsPipelines.Where(r => PairOf(r) == PairOf(program)).ToArray();
                Assert.NotEmpty(captured);
                var reflected = MetalShaderBuild.Pair(program.VertexGlsl, program.FragmentGlsl, null, program.Name);
                foreach (var request in captured)
                {
                    var layouts = request.Description.ResourceLayouts
                        .Select(layout => Assert.IsType<FakeResourceLayout>(layout).Description).ToArray();
                    // Real pipeline declarations must agree with the shader before any Metal device is created.
                    reflected.Table.RequireLayoutShape(layouts, program.Name);
                    Assert.Equal(program.Name == "PreparedFoliageDepth" ? new[] { 1, 1, 0, 1 } : new[] { 1, 1, 2, 1 },
                        layouts.Select(layout => layout.Elements?.Length ?? 0));
                }
            }
        }

        [Fact]
        public void CompletenessDistinguishesProgramsThatShareAVertexSource()
        {
            ShippedGraphicsProgram model = ShippedShaderPrograms.GraphicsPrograms()
                .Single(program => program.Name == "Model");
            ShippedGraphicsProgram dissolve = ShippedShaderPrograms.GraphicsPrograms()
                .Single(program => program.Name == "ModelDissolve");
            FakeGraphicsPipelineRequest[] captured =
            [
                new FakeGraphicsPipelineRequest(model.VertexGlsl, model.FragmentGlsl, default),
            ];

            Assert.Equal(["ModelDissolve"], MissingPrograms([model, dissolve], captured));
        }

        static string[] MissingPrograms(IEnumerable<ShippedGraphicsProgram> programs,
            IEnumerable<FakeGraphicsPipelineRequest> captured)
        {
            var capturedPairs = captured.Select(PairOf).ToHashSet();
            return programs
                .Where(program => Parse(program.VertexGlsl).Count > 0
                    && !capturedPairs.Contains(PairOf(program)))
                .Select(program => program.Name)
                .ToArray();
        }

        static ShaderPair PairOf(ShippedGraphicsProgram program)
            => new(program.VertexGlsl, program.FragmentGlsl);

        static ShaderPair PairOf(FakeGraphicsPipelineRequest request)
            => new(request.VertexGlsl, request.FragmentGlsl);

        static void CaptureClipmapPipeline(FakeGpuDevice device, GpuOutputDescription outputs,
            IGpuCommandList commands)
        {
            using var resources = new RenderResources(device, 64, 64, false);
            using var water = new WaterRenderer(device, outputs);
            var settings = new WaterSettings
            {
                GridMode = WaterGridMode.Clipmap,
                WaveSource = WaterWaveSource.Procedural,
            };
            WaterPlane[] planes = [new WaterPlane(0f, 0f, 0f, 16f)];
            water.PrepareFrame(new FramePrepare(settings, planes, 0f));
            water.Draw(commands, resources, planes, Matrix4x4.Identity, Matrix4x4.Identity, -Vector3.UnitY, Color.White,
                new Vector3(0f, 4f, 0f), settings, new SkySettings(), 0f);
        }

        // The triangle optics programs are created only when a bound triangle field is prepared, once per geometry.
        static void CaptureTriangleOpticsPipelines(FakeGpuDevice device, GpuOutputDescription outputs)
        {
            using var resources = new RenderResources(device, 64, 64, false);
            using var water = new WaterRenderer(device, outputs);
            WaterOpticsHandle field = water.Optics.LoadTriangles(KhaozEngine.Tests.Render3D.WaterOpticsTriangleResourceTests.Field());
            var look = new WaterLook { SwellAmplitude = .01f, SwellSteepness = 0f };
            WaterPlane[] planes = [new WaterPlane(1f, 0f, 1f, .25f, look: look,
                optics: new WaterOpticsBinding(field, Vector3.Zero, new Vector2(2f)))];
            foreach (WaterGridMode mode in new[] { WaterGridMode.CameraFocused, WaterGridMode.Clipmap })
            {
                var settings = new WaterSettings { GridMode = mode, WaveSource = WaterWaveSource.Procedural };
                var frame = new FramePrepare(settings, planes, 0f);
                water.PrepareFrame(frame);
                water.PrepareOpticsFrame(resources, frame);
            }
        }

        static void CaptureTargetOutlinePipelines(Scene3D scene, IGpuFramebuffer framebuffer,
            IGpuCommandList commands)
        {
            MeshHandle rigid = scene.LoadMesh(MeshPrimitives.Box(1f));
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6, 3, Axis.Z);
            SkinnedMeshHandle skinned = scene.LoadSkinnedMesh(tube);
            scene.Post.Starfield = false;
            scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            scene.Begin();
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f);
            scene.DrawMeshOutline(group, rigid, Matrix4x4.Identity);
            scene.DrawSkinnedOutline(group, skinned, tube.RestPose, Matrix4x4.Identity);
            scene.PrepareFrame();
            scene.RenderInternal(commands, 64, 64, framebuffer);
        }

        // The temporal variants are built only against a model target that carries the motion attachment, so a scene
        // that never renders temporally captures none of them. Building the renderers against that target captures all.
        static void CaptureTemporalPipelines(FakeGpuDevice device, IGpuCommandList commands, GpuOutputDescription? outputs = null)
        {
            GpuOutputDescription target = outputs ?? ModelTargets.Temporal;
            using var model = new ModelRenderer(device, target, 64, 1);
            model.UploadFoliageUniforms(commands, [default]);
            using var anchorBuffer = device.Factory.CreateBuffer(new(32, GpuBufferUsage.UniformBuffer));
            using var anchor = model.CreatePreparedFoliageAnchorSet(anchorBuffer);
            using var vertices = device.Factory.CreateBuffer(new(3 * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
            using var instances = device.Factory.CreateBuffer(new(ModelRenderer.FoliageInstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
            using var indices = device.Factory.CreateBuffer(new(6, GpuBufferUsage.IndexBuffer));
            model.DrawPreparedFoliageMesh(commands, vertices, indices, 3, GpuIndexFormat.UInt16,
                instances, 0, 1, 0, null, anchor);
            using var billboards = new TexturedBillboardRenderer(device, target);
            using var beams = new BeamRenderer(device, target);
            using var trails = new TrailRenderer(device, target);
            using var overlays = new OverlayMeshRenderer(device, target);
            using var silhouettes = new SilhouetteRenderer(device, target);
        }

        static void CaptureFoliageShadowPipelines(Scene3D scene, IGpuFramebuffer framebuffer,
            IGpuCommandList commands)
        {
            MeshHandle opaque = scene.LoadMesh(MeshPrimitives.Tile(.8f, 2f));
            var texture = scene.LoadTexture([255, 255, 255, 255], 1, 1);
            MeshHandle cutout = scene.LoadMesh(MeshPrimitives.Tile(.8f, 2f),
                new Scene3D.SurfaceMaps(texture, alphaCutoff: .5f));
            using FoliageBatch casters = scene.CreateFoliageBatch(
                [new(opaque, Matrix4x4.Identity, .1f, CastsShadows: true),
                 new(cutout, Matrix4x4.Identity, .1f, CastsShadows: true)]);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Camera.Frame(Vector3.Zero, new Vector3(4f));
            scene.Begin();
            scene.DrawFoliage(casters, Vector3.Zero,
                new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f });
            scene.PrepareFrame();
            scene.RenderInternal(commands, 64, 64, framebuffer);
            Assert.True(scene.FoliageShadowDrawCalls > 0);
        }

        static void CapturePreparedFoliagePipelines(FakeGpuDevice device)
        {
            using var host = new KhaozEngine.Tests.MapDoc.NativeUploadTestScene(device);
            var scene = host.Scene;
            KhaozEngine.Tests.MapDoc.NativeResourceUploadTests.LoadParts(scene);
            var data = new SceneUploadData(null, null,
                KhaozEngine.Tests.MapDoc.NativeRenderAssetFixtures.FoliageSource(), default);
            var handle = KhaozEngine.Tests.MapDoc.NativeResourceUploadTests.Publish(scene, data);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Begin();
            scene.DrawPreparedFoliage(handle, Vector3.Zero, new() { DrawRadius = 100, DistantDensity = 1 }, default);
            scene.AddLight(new(1, 2, 1), Color.White, 6, 1, LightShadow.Static(71));
            host.Render();
            Assert.True(scene.FoliageShadowDrawCalls > 0);
            // The first request admits the atlas at the next explicit frame boundary.
            scene.Begin();
            scene.DrawPreparedFoliage(handle, Vector3.Zero, new() { DrawRadius = 100, DistantDensity = 1 }, default);
            scene.AddLight(new(1, 2, 1), Color.White, 6, 1, LightShadow.Static(71));
            host.Render();
            Assert.True(scene.FoliagePointShadowDrawCalls > 0);
            scene.ForceTemporalForTests = true;
            scene.Begin();
            scene.DrawPreparedFoliage(handle, Vector3.Zero, new() { DrawRadius = 100, DistantDensity = 1 }, default);
            host.Render();
            scene.ReleaseResource(handle);
        }

        static IReadOnlyList<VertexDeclaration> Parse(string glsl)
        {
            var declarations = new List<VertexDeclaration>();
            foreach (Match match in VertexInput.Matches(glsl))
            {
                declarations.Add(new VertexDeclaration(
                    uint.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    FormatOf(match.Groups[2].Value),
                    match.Groups[3].Value));
            }
            return declarations;
        }

        static void CaptureBoundedWaterPipeline(Scene3D scene)
        {
            var mesh = new GltfMesh(new[] { new ModelVertex(new(0, 0, 0), Vector3.UnitY, Vector4.One),
                new ModelVertex(new(0, 0, 2), Vector3.UnitY, Vector4.One), new ModelVertex(new(2, 0, 0), Vector3.UnitY, Vector4.One) }, new uint[] { 0, 1, 2 });
            var handle = scene.LoadWaterMesh(mesh, WaterSurfaceDomain.Encode(new[] { new Vector2(0, 0), new(0, 2), new(2, 0) }));
            scene.UnloadMesh(handle);
        }

        static GpuVertexElementFormat FormatOf(string glslType) => glslType switch
        {
            "float" => GpuVertexElementFormat.Float1,
            "vec2" => GpuVertexElementFormat.Float2,
            "vec3" => GpuVertexElementFormat.Float3,
            "vec4" => GpuVertexElementFormat.Float4,
            _ => throw new ArgumentOutOfRangeException(nameof(glslType), glslType, null),
        };

        readonly record struct VertexDeclaration(uint Location, GpuVertexElementFormat Format, string Name);
        readonly record struct ShaderPair(string VertexGlsl, string FragmentGlsl);
    }
}
