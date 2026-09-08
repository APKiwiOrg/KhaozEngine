using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
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
            water.Draw(commands, resources, planes, Matrix4x4.Identity, -Vector3.UnitY, Color.White,
                new Vector3(0f, 4f, 0f), settings, new SkySettings(), 0f);
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
