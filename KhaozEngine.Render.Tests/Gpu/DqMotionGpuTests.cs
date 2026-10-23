using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqMotionGpuTests(DqMotionScene fixture, ITestOutputHelper output) : IClassFixture<DqMotionScene>
{
    [GpuFact]
    public void DQ_motion_matches_known_current_and_previous_surfaces()
    {
        IGpuDevice device = fixture.Device;
        output.WriteLine($"backend={device.Backend} adapter={device.Capabilities.DeviceName}");
        var vertex = new SkinnedVertex
        {
            Position = new Vector3(.25f, -.5f, .125f),
            Normal = Vector3.UnitZ,
            Color = Vector4.One,
            BoneIndices = new Vector4(0, 1, 0, 0),
            BoneWeights = new Vector4(.5f, .5f, 0, 0),
        };
        // Identity plus an exact half-turn blends to a quarter-turn under DQ. LBS collapses XY to zero.
        // Current identity is deliberate: the red witness isolates missing PREVIOUS DQ evaluation.
        Vector3 expectedPrevious = new(.5f, .25f, .125f);
        foreach (bool rotatedCurrent in new[] { false, true })
            foreach (bool history in new[] { true, false })
            {
                var native = DqMotionWitness.Capture(device, vertex, history, rotatedCurrent);
                Vector3 expectedCurrent = rotatedCurrent ? new Vector3(-.25f, .5f, .125f) : vertex.Position;
                AssertPosition(expectedCurrent, native.Current);
                AssertPosition(history ? expectedPrevious : expectedCurrent, native.Previous);
                Vector2 expectedMotion = !history ? Vector2.Zero : rotatedCurrent
                    ? new Vector2(-.375f, -.125f) : new Vector2(-.125f, .375f);
                Assert.InRange(Vector2.Distance(expectedMotion, native.Motion), 0f, 2e-5f);
                if (!history) Assert.Equal(Vector2.Zero, native.Motion);
                Vector3 cpu = SkinningEvaluation.Position(vertex, SkinningProfile.DualQuaternionRigid, [],
                    DqMotionWitness.Prepare(DqMotionWitness.Previous));
                AssertPosition(expectedPrevious, cpu);
            }

        int cases = 0;
        foreach (bool gpu in new[] { false, true })
            foreach (bool dissolve in new[] { false, true })
                foreach (string scenario in DqMotionScene.Scenarios)
                {
                    var capture = fixture.Capture(gpu, dissolve, scenario);
                    capture.AssertMotion();
                    output.WriteLine($"gpu={gpu} dissolve={dissolve} scenario={scenario} drawn={capture.Drawn}");
                    cases++;
                }
        // The shared scene has crossed methods, recycled handles, execution locations, materials and origins.
        var aged = fixture.Capture(true, true, "joint");
        using var fresh = new DqMotionScene(device);
        var baseline = fresh.Capture(true, true, "joint");
        aged.AssertMotion();
        baseline.AssertMotion();
        Assert.Equal(baseline.Motion.Motion, aged.Motion.Motion);
        output.WriteLine($"matched={cases}/{DqMotionScene.Scenarios.Length * 4} scene cases, " +
            "current/previous model-space word readback, actual RG16F motion, shared/fresh motion words equal");
    }

    static void AssertPosition(Vector3 expected, Vector3 actual)
    {
        const float L = 1f;
        for (int axis = 0; axis < 3; axis++)
            Assert.InRange(MathF.Abs(actual[axis] - expected[axis]), 0f, 2e-5f * L);
    }
}

/// <summary>Production temporal vertex arithmetic with only its output transport replaced by exact word capture.
/// The existing LBS temporal source is the red baseline when the DQ variant does not exist yet.</summary>
internal static class DqMotionWitness
{
    internal static string Vertex => (string?)typeof(ShaderSources).GetField("DqSkinnedModelMotionVert",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        ?? ShaderSources.SkinnedModelMotionVert;
    internal static bool HasDq => Vertex != ShaderSources.SkinnedModelMotionVert;
    internal static Matrix4x4[] Current => [Matrix4x4.Identity, Matrix4x4.Identity];
    internal static Matrix4x4[] Previous => [Matrix4x4.Identity,
        new(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)];
    internal static RigidDualQuaternion[] Prepare(Matrix4x4[] matrices) => matrices.Select((m, i) =>
        DualQuaternionPalette.FromComposed(m, new("synthetic temporal witness", null, 0), i)).ToArray();

    internal static string CaptureVertex()
    {
        string source = Vertex;
        source = "#version 450\nconst mat4 ViewProj = mat4(1.0);\nconst mat4 Model = mat4(1.0);\nconst mat4 P = mat4(1.0);\n"
            + source[source.IndexOf("layout(set=2, binding=0) uniform Palette", StringComparison.Ordinal)..];
        source = source.Replace("set=2, binding=0", "set=0, binding=0", StringComparison.Ordinal)
            .Replace("set=3, binding=", "set=1, binding=", StringComparison.Ordinal);
        source = Regex.Replace(source, @"layout\(location=\d+\) out ([^;]+);", "$1;");
        source = source.Replace("void main() {", @"
layout(location=0) flat out uvec3 capturedCurrent;
layout(location=1) flat out uvec3 capturedPrevious;
void main() {", StringComparison.Ordinal);
        return ShaderText.BeforeEndOfMain(source, @"
    capturedCurrent = floatBitsToUint(vCurClip.xyz);
    capturedPrevious = floatBitsToUint(vPrevClip.xyz);
    vec2 clip = gl_VertexIndex == 0 ? vec2(-1,-1) : gl_VertexIndex == 1 ? vec2(3,-1) : vec2(-1,3);
    gl_Position = vec4(clip, (Normal.x + Normal.y + Normal.z + Color.x + Color.y + Color.z + Color.w
        + TexCoord.x + TexCoord.y + Tangent.x + Tangent.y + Tangent.z + Tangent.w) * 1e-30, 1);
");
    }

    const string Fragment = @"#version 450
layout(location=0) flat in uvec3 capturedCurrent;
layout(location=1) flat in uvec3 capturedPrevious;
layout(location=0) out vec4 word;
void main() {
    int i = int(gl_FragCoord.x);
    uint bits = i < 3 ? capturedCurrent[i] : capturedPrevious[i-3];
    uvec4 bytes = uvec4(bits, bits >> 8, bits >> 16, bits >> 24) & uvec4(255);
    word = vec4(bytes) / 255.0;
}";

    internal static (Vector3 Current, Vector3 Previous, Vector2 Motion) Capture(IGpuDevice device,
        SkinnedVertex vertex, bool history, bool rotatedCurrent)
    {
        IGpuResourceFactory f = device.Factory;
        using var texture = f.CreateTexture(GpuTextureDescription.Texture2D(6, 1,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var target = f.CreateFramebuffer(null, texture);
        using var shaders = f.CreateShadersFromSpirv(CaptureVertex(), Fragment);
        using var currentLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Palette", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
        using var previousLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("MotionFrame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("PrevPalette", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
        using var current = f.CreateBuffer(new GpuBufferDescription(HasDq ? 4096u : 8192u, GpuBufferUsage.UniformBuffer));
        using var previous = f.CreateBuffer(new GpuBufferDescription(HasDq ? 4352u : 8448u, GpuBufferUsage.UniformBuffer));
        using var frame = f.CreateBuffer(new GpuBufferDescription(MotionFrameUbo.SizeInBytes, GpuBufferUsage.UniformBuffer));
        device.UpdateBuffer(frame, 0, new MotionFrameUbo
        { CurViewProj = Matrix4x4.Identity, PrevViewProj = Matrix4x4.Identity, Params = new Vector4(history ? 1 : 0, 0, 0, 0) });
        device.UpdateBuffer(previous, 0, Matrix4x4.Identity);
        Matrix4x4[] currentPose = rotatedCurrent ? [Previous[1], Previous[1]] : Current;
        if (HasDq)
        {
            device.UpdateBuffer(current, 0, Prepare(currentPose));
            device.UpdateBuffer(previous, 64, Prepare(Previous));
        }
        else
        {
            device.UpdateBuffer(current, 0, currentPose);
            device.UpdateBuffer(previous, 64, Previous);
        }
        using var currentSet = f.CreateResourceSet(new GpuResourceSetDescription(currentLayout, current));
        using var previousSet = f.CreateResourceSet(new GpuResourceSetDescription(previousLayout, frame, previous));
        using var vertices = f.CreateBuffer(new GpuBufferDescription(3 * SkinnedVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        device.UpdateBuffer(vertices, 0, new[] { vertex, vertex, vertex });
        using var pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ShaderSet = shaders,
            ResourceLayouts = [currentLayout, previousLayout],
            VertexLayouts = new List<GpuVertexLayoutDescription> { DqGpuReadback.VertexLayout },
            Outputs = target.Outputs,
        });
        using var commands = f.CreateCommandList();
        using (GpuRecording.Open(device, commands, "DQ previous/current word capture"))
        {
            commands.SetFramebuffer(target);
            commands.ClearColorTarget(0, Color.White);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, currentSet);
            commands.SetGraphicsResourceSet(1, previousSet);
            commands.SetVertexBuffer(0, vertices);
            commands.Draw(3);
        }
        device.Submit(commands);
        byte[] bytes = GpuReadback.ToRgba(device, texture, 6, 1);
        float[] words = new float[6];
        for (int i = 0; i < words.Length; i++)
            words[i] = BitConverter.UInt32BitsToSingle((uint)bytes[4 * i] | ((uint)bytes[4 * i + 1] << 8)
                | ((uint)bytes[4 * i + 2] << 16) | ((uint)bytes[4 * i + 3] << 24));
        Vector3 now = new(words[0], words[1], words[2]), then = new(words[3], words[4], words[5]);
        return (now, then, new Vector2((now.X - then.X) * .5f, (now.Y - then.Y) * -.5f));
    }

    internal static PreparedSkinningPose Queue(Scene3D scene, SkinnedGltfMesh mesh, SkinnedMeshHandle handle,
        MotionKey key, Matrix4x4 world, Matrix4x4[] joints, float dissolve)
    {
        // Use the retained upload snapshot, including for recycled handles. Public admission stays gated until S3.11.
        var entries = (System.Collections.IList)DqGpuReadback.Field(scene, "_skinnedMeshes");
        var source = (SkinnedGltfMesh)DqGpuReadback.Field(entries[handle.Index]!, "Source");
        Assert.Equal(mesh.Profile, source.Profile);
        var draw = new SkinnedInstanceDraw(handle, world)
        {
            Motion = key,
            CastsShadows = false,
            Tint = Color.White,
            Material = Material.None,
            Dissolve = dissolve,
            DissolveEdgeWidth = .01f,
            DissolveEdgeColor = Color.White,
        };
        PreparedSkinningPose pose = DqGpuReadback.Queue(scene, source, draw, joints);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bounds = (MeshBounds)typeof(Scene3D).GetMethod("SkinnedMotionBounds", flags)!
            .Invoke(scene, [entries[handle.Index], pose, draw])!;
        typeof(Scene3D).GetMethod("RecordSkinnedMotion", flags)!.Invoke(scene, [draw, pose, bounds]);
        return pose;
    }

    internal static SkinnedGltfMesh Patch(SkinningProfile profile)
    {
        SkinnedGltfMesh mesh = DqGpuReadback.Patch(profile);
        for (int i = 0; i < 4; i++)
            mesh.Vertices[i].Position = new Vector3(i is 1 or 2 ? .35f : -.35f, i >= 2 ? .35f : -.35f, 0);
        return mesh;
    }
}

public sealed class DqMotionScene : IDisposable
{
    const int Width = 256, Height = 128;
    internal static readonly string[] Scenarios = ["joint", "placement", "absent", "mesh", "method", "origin"];
    readonly IGpuDevice? _shared;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    int _sequence;
    public DqMotionScene() { }
    internal DqMotionScene(IGpuDevice device) => _shared = device;
    internal IGpuDevice Device => _shared ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;

    internal CaptureResult Capture(bool gpu, bool dissolve, string scenario)
    {
        if (_preview is null)
        {
            _preview = new Render3DPreview(Device, Width, Height);
            Configure(_preview.Scene);
        }
        Scene3D scene = _preview.Scene;
        scene.UseGpuSkinning = gpu;
        scene.RenderOrigin = Vector3.Zero;
        var meshes = new[] { DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid),
            DqMotionWitness.Patch(SkinningProfile.LinearBlend), DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid),
            DqMotionWitness.Patch(SkinningProfile.LinearBlend) };
        SkinnedMeshHandle[] handles = meshes.Select(scene.LoadSkinnedMesh).ToArray();
        MotionKey[] keys = Enumerable.Range(0, 4).Select(i => MotionKey.From((ulong)(100 + 4 * _sequence + i))).ToArray();
        _sequence++;
        Matrix4x4[] oldPose = scenario == "placement" ? DqMotionWitness.Current : DqMotionWitness.Previous;
        float threshold = dissolve ? .25f : 0;
        Matrix4x4 World(int actor, bool current) => Matrix4x4.CreateTranslation(-3 + actor * 2 +
            (current && scenario == "placement" ? .15f : 0), current && scenario == "placement" ? .12f : 0, 0);
        scene.CameraCut();
        _preview.Capture(s =>
        {
            for (int i = 0; i < 4; i++)
                if (scenario != "absent" || i != 0)
                    DqMotionWitness.Queue(s, meshes[i], handles[i], keys[i], World(i, false), oldPose, threshold);
        });
        // With no previous frame the shader aliases previous clip to current clip, exactly. Actor-local
        // current-data seeding on a continuing frame instead keeps the established projection rounding bound.
        Assert.Null(scene.PreviousFrameView);
        MotionTargetReadback firstSight = scene.ReadMotionTargetForTests();
        Assert.True(MotionExpectation.AssertDrawnPixels(firstSight, (_, _) => Vector2.Zero, 0f) > 20);
        for (int y = 0; y < firstSight.Height; y++)
            for (int x = 0; x < firstSight.Width; x++)
                if (!firstSight.IsBackground(x, y))
                {
                    Vector2 firstSightMotionWithStationaryCamera = firstSight.UvAt(x, y);
                    Assert.Equal(Vector2.Zero, firstSightMotionWithStationaryCamera);
                }
        Device.Present();
        IsoCamera3D oldCamera = MotionExpectation.Snapshot(scene.Camera);
        if (scenario is "mesh" or "method")
        {
            int count = scenario == "method" ? 2 : 1;
            for (int i = 0; i < count; i++)
            {
                SkinnedMeshHandle old = handles[i];
                scene.UnloadSkinnedMesh(old);
                if (scenario == "method") meshes[i] = DqMotionWitness.Patch(i == 0
                    ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
                handles[i] = scene.LoadSkinnedMesh(meshes[i]);
                Assert.Equal(old.Index, handles[i].Index);
                Assert.NotEqual(old.Generation, handles[i].Generation);
            }
        }
        if (scenario == "origin") scene.RenderOrigin = new Vector3(128, 0, 128);
        _preview.Capture(s =>
        {
            for (int i = 0; i < 4; i++)
                DqMotionWitness.Queue(s, meshes[i], handles[i], keys[i], World(i, true),
                    i == 2 ? [DqMotionWitness.Previous[1], DqMotionWitness.Previous[1]] : DqMotionWitness.Current, threshold);
        });
        MotionTargetReadback motion = scene.ReadMotionTargetForTests();
        Assert.NotNull(scene.PreviousFrameView);
        Device.Present();
        IsoCamera3D camera = MotionExpectation.Snapshot(scene.Camera);
        var actors = new ActorExpectation[4];
        for (int i = 0; i < 4; i++)
        {
            bool seed = i == 0 && (scenario is "absent" or "mesh" or "method") || i == 1 && scenario == "method";
            // These are literal independent surfaces. Half-turn blended with identity is a DQ quarter-turn,
            // or collapsed XY for LBS. Actor B also proves a nonidentity current DQ surface.
            Vector3 Previous(Vector3 p) => seed || scenario == "placement" ? p : i % 2 == 0
                ? new Vector3(-p.Y, p.X, p.Z) : new Vector3(0, 0, p.Z);
            Vector3 Current(Vector3 p) => i == 2 ? new Vector3(-p.X, -p.Y, p.Z) : p;
            int[] corners = [0, 1, 3];
            Vector2[] now = corners.Select(v => Project(camera, Vector3.Transform(Current(meshes[i].Vertices[v].Position), World(i, true)))).ToArray();
            Vector2[] then = corners.Select(v => Project(oldCamera,
                Vector3.Transform(Previous(meshes[i].Vertices[v].Position), World(i, seed)))).ToArray();
            actors[i] = new ActorExpectation(now, then, scene.CurrentFrameView.JitterPixels);
        }
        foreach (SkinnedMeshHandle handle in handles) scene.UnloadSkinnedMesh(handle);
        return new CaptureResult(motion, actors);
    }

    internal static void Configure(Scene3D scene)
    {
        scene.ForceTemporalForTests = true;
        scene.FrustumCulling = false;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Camera.Frame(Vector3.Zero, new Vector3(0, 0, 8));
        scene.Camera.OrthoSize = 4;
    }

    static Vector2 Project(IsoCamera3D camera, Vector3 p)
    {
        Assert.True(camera.WorldToScreen(p, Width, Height, out Vector2 screen));
        return screen;
    }

    internal sealed record ActorExpectation(Vector2[] Now, Vector2[] Then, Vector2 Jitter)
    {
        internal bool Contains(int x, int y) => x + .5f >= Now.Min(p => p.X) - 1 && x + .5f <= Now.Max(p => p.X) + 1
            && y + .5f >= Now.Min(p => p.Y) - 1 && y + .5f <= Now.Max(p => p.Y) + 1;
        internal Vector2 MotionAt(int x, int y)
        {
            Vector2 p = new Vector2(x + .5f, y + .5f) - Jitter;
            Vector2 a = Now[1] - Now[0], b = Now[2] - Now[0], d = p - Now[0];
            float det = a.X * b.Y - a.Y * b.X;
            float u = (d.X * b.Y - d.Y * b.X) / det, v = (a.X * d.Y - a.Y * d.X) / det;
            return p - (Then[0] + u * (Then[1] - Then[0]) + v * (Then[2] - Then[0]));
        }
    }

    internal sealed record CaptureResult(MotionTargetReadback Motion, ActorExpectation[] Actors)
    {
        internal int Drawn { get; private set; }
        internal void AssertMotion()
        {
            Drawn = 0;
            foreach (ActorExpectation actor in Actors)
            {
                int count = MotionExpectation.AssertDrawnPixels(Motion, actor.MotionAt, .08f, actor.Contains);
                Assert.True(count > 20, $"Only {count} actor pixels drew.");
                Drawn += count;
            }
        }
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _context?.Dispose();
    }
}
