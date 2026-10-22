using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Captures the ordinary material vertex's actual local attributes into ten R32F texels.
/// Only the world/clip/output tail is replaced. The production palette, blend and direction graph remain.
/// Reflection keeps this test revision runnable against the pre-DQ renderer for the hosted red proof.</summary>
internal static class DqGpuReadback
{
    internal static string MaterialVertex => (string?)typeof(ShaderSources).GetField("DqSkinnedModelVert",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? ShaderSources.SkinnedModelVert;

    internal static string? Helper(string palette) => (string?)typeof(ShaderSources).Assembly
        .GetType("KhaozEngine.Render3D.Internal.DualQuaternionGlsl")?
        .GetMethod("Source", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, [palette]);

    internal static GpuVertexLayoutDescription VertexLayout => new(
        new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
        new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
        new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
        new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
        new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
        new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4),
        new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));

    internal static ModelVertex Capture(IGpuDevice device, in SkinnedVertex source,
        RigidDualQuaternion[] dq, Matrix4x4[] lbs)
    {
        string production = MaterialVertex;
        bool dualQuaternion = production != ShaderSources.SkinnedModelVert;
        int paletteStart = production.IndexOf("layout(set=2, binding=0) uniform Palette", StringComparison.Ordinal);
        int worldStart = production.IndexOf("    vec4 world = Model * localPos;", StringComparison.Ordinal);
        string vertex = "#version 450\n" + production[paletteStart..worldStart]
            .Replace("set=2, binding=0", "set=0, binding=0", StringComparison.Ordinal);
        vertex = Regex.Replace(vertex, @"layout\(location=\d+\) out [^\n]+\n", "");
        vertex = vertex.Replace("void main() {", @"
layout(location=0) flat out vec3 capturedPosition;
layout(location=1) flat out vec3 capturedNormal;
layout(location=2) flat out vec4 capturedTangent;
void main() {", StringComparison.Ordinal) + @"
    capturedPosition = localPos.xyz;
    capturedNormal = nLocal;
    capturedTangent = tLocal;
    vec2 clip = gl_VertexIndex == 0 ? vec2(-1,-1) : gl_VertexIndex == 1 ? vec2(3,-1) : vec2(-1,3);
    // Keep the full material vertex input signature live on FXC, without touching the captured words.
    gl_Position = vec4(clip, (Color.x + Color.y + Color.z + Color.w + TexCoord.x + TexCoord.y) * 1e-30, 1);
}";
        const string fragment = @"#version 450
layout(location=0) flat in vec3 capturedPosition;
layout(location=1) flat in vec3 capturedNormal;
layout(location=2) flat in vec4 capturedTangent;
layout(location=0) out float word;
void main() {
    int i = int(gl_FragCoord.x);
    word = i < 3 ? capturedPosition[i] : i < 6 ? capturedNormal[i-3] : capturedTangent[i-6];
}";
        var factory = device.Factory;
        using var texture = factory.CreateTexture(GpuTextureDescription.Texture2D(10, 1,
            GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget));
        using var target = factory.CreateFramebuffer(null, texture);
        using var shaders = factory.CreateShadersFromSpirv(vertex, fragment);
        using var layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Palette", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
        using var palette = factory.CreateBuffer(new GpuBufferDescription(dualQuaternion ? 4096u : 8192u,
            GpuBufferUsage.UniformBuffer));
        if (dualQuaternion) device.UpdateBuffer(palette, 0, dq);
        else device.UpdateBuffer(palette, 0, lbs);
        using var set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, palette));
        using var vertices = factory.CreateBuffer(new GpuBufferDescription(3 * SkinnedVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        device.UpdateBuffer(vertices, 0, new[] { source, source, source });
        using var pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            VertexLayouts = new List<GpuVertexLayoutDescription> { VertexLayout },
            ShaderSet = shaders,
            ResourceLayouts = [layout],
            Outputs = target.Outputs,
        });
        using var commands = factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "DQ material attribute capture"))
        {
            commands.SetFramebuffer(target);
            commands.ClearColorTarget(0, new KhaozEngine.Primitives.Color(12345, 12345, 12345, 1));
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.SetVertexBuffer(0, vertices);
            commands.Draw(3);
        }
        device.Submit(commands);
        float[] words = TemporalTextureIo.Read(device, texture);
        return new ModelVertex(new Vector3(words[0], words[1], words[2]), new Vector3(words[3], words[4], words[5]),
            source.Color, source.Uv, new Vector4(words[6], words[7], words[8], words[9]));
    }

    // Stage 3 keeps public submission closed until every consumer is ready. Tests queue an owned prepared
    // pose directly, with no production bypass switch and no DQ actor sent through public LBS consumers.
    internal static PreparedSkinningPose Queue(Scene3D scene, SkinnedGltfMesh mesh, in SkinnedInstanceDraw draw,
        Matrix4x4[] joints)
    {
        var store = (PreparedSkinningStore)Field(scene, "_preparedSkinning");
        PreparedSkinningPose pose = store.Prepare(mesh, joints, new("synthetic material", null, 0));
        var queue = (SkinnedSceneInstances)Field(scene, "_skinnedInstances");
        int slot = queue.Items.Count;
        var poses = (PreparedSkinningPose[])Field(scene, "_submittedSkinningPoses");
        if (poses.Length <= slot) Array.Resize(ref poses, slot + 1);
        poses[slot] = pose;
        typeof(Scene3D).GetField("_submittedSkinningPoses", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scene, poses);
        var matrices = (List<Matrix4x4>)Field(scene, "_boneMatrices");
        for (int i = 0; i < SkinningMath.MaxBonesPerDraw; i++)
            matrices.Add(i < joints.Length ? SkinningMath.Compose(joints[i], mesh.InverseBind[i]) : Matrix4x4.Identity);
        queue.Add(in draw);
        return pose;
    }

    internal static object Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    internal static Matrix4x4[] Twist => [Matrix4x4.Identity,
        new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)];

    internal static SkinnedGltfMesh Patch(SkinningProfile profile, bool tangent = true, bool right = false)
    {
        var positions = right
            ? new[] { new Vector3(.2f, -.8f, 0), new Vector3(.8f, -.8f, 0), new Vector3(.8f, .8f, 0), new Vector3(.2f, .8f, 0) }
            : new[] { new Vector3(-.8f, -.8f, 0), new Vector3(-.2f, -.8f, 0), new Vector3(-.2f, .8f, 0), new Vector3(-.8f, .8f, 0) };
        var vertices = new SkinnedVertex[4];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new SkinnedVertex
        {
            Position = positions[i], Normal = Vector3.UnitZ, Color = Vector4.One,
            Uv = new Vector2(i is 1 or 2 ? 1 : 0, i >= 2 ? 1 : 0),
            BoneIndices = new Vector4(0, 1, 0, 0), BoneWeights = new Vector4(.5f, .5f, 0, 0),
            Tangent = tangent ? new Vector4(1, 0, 0, -1) : Vector4.Zero,
        };
        return new SkinnedGltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 },
            [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity], null, profile);
    }
}
