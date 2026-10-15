using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using KhaozEngine.Tests.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Lazy hosted GPU fixture with reversed garment joints and independent literal controls.</summary>
public sealed class SkinnedAttachmentGpuScene : IDisposable
{
    internal const int W = 480, H = 320;
    internal enum Control { Attachments, Skinned, Rigid }
    static readonly Color Red = new(.8f, .1f, .1f, 1);
    static readonly Color Green = new(.1f, .8f, .1f, 1);
    static readonly Color Blue = new(.1f, .2f, 1, 1);
    static readonly Matrix4x4 Model = Matrix4x4.CreateTranslation(10, 0, 0);
    static readonly Matrix4x4 RigidLocal = Matrix4x4.CreateScale(.75f) * Matrix4x4.CreateTranslation(-3, 3, 0);
    static readonly Vector3[] GarmentPositions = [new(.5f, .5f, 0), new(3.5f, .5f, 0), new(.5f, 3.5f, 0)];
    static readonly float[] UpperWeights = [.25f, .5f, .75f];
    readonly Skeleton _skeleton = new([-1, 0], [At(0, 1), At(0, 2)], [0, 1], [0, 1], ["hip", "upper"]);
    TemporalFixture? _fixture;
    AttachmentSetCache? _cache;
    BoundAttachmentSet? _bound;
    SkinnedMeshHandle _host, _garmentControl;
    MeshHandle _rigid, _floor, _restGarment, _posedGarment;
    Matrix4x4[] _rest = [], _posed = [];

    internal string Backend => Fixture.Device.Backend.ToString();
    internal TemporalFixture Fixture { get { EnsureScene(); return _fixture!; } }

    internal byte[] Capture(Control control, bool gpuSkinning, bool posed = true, float dissolve = 0,
        float complement = 0, bool invert = false, int light = 0, bool shadowOnly = false,
        bool castsShadows = true, bool outlines = false, bool receiverOnly = false)
    {
        EnsureScene();
        Configure(_fixture!.Scene, gpuSkinning, light);
        void DrawFrame(Scene3D scene, int _)
        {
            scene.EffectTimeSeconds = 0;
            if (light != 0)
            {
                scene.Draw(_floor, Model, new Color(.65f, .65f, .65f, 1));
                if (light == 2)
                    scene.AddLight(new Vector3(6, 7, 5), Color.White, 25, 2.5f, LightShadow.Dynamic);
            }
            if (receiverOnly) return;
            if (!shadowOnly)
                scene.DrawSkinned(new SkinnedInstanceDraw(_host, Model) { Tint = Green, CastsShadows = false },
                    posed ? _posed : _rest);
            var template = new RigidInstanceDraw(default, Model)
            {
                Tint = Red, Dissolve = dissolve, DissolveComplement = complement, InvertShadowDissolve = invert,
                ShadowOnly = shadowOnly, CastsShadows = castsShadows,
            };
            Draw(scene, control, posed, template);
            if (!outlines) return;
            MeshOutlineGroup group = scene.BeginMeshOutline(Blue, 2);
            if (control == Control.Attachments)
                _bound!.QueueOutline(_skeleton, posed ? _posed : _rest, Model, group, dissolve, complement > .5f);
            else
            {
                scene.DrawSkinnedOutlineDissolved(group, _garmentControl, ControlPose(posed), Model, dissolve, complement > .5f);
                scene.DrawMeshOutlineDissolved(group, _rigid, RigidWorld(posed), dissolve, complement > .5f);
            }
        }
        // Point shadows allocate at the next Begin after the first request. Compare settled captures in both scenes.
        if (light == 2) _fixture.Frame(DrawFrame);
        return _fixture.Frame(DrawFrame);
    }

    void Draw(Scene3D scene, Control control, bool posed, RigidInstanceDraw template)
    {
        if (control == Control.Attachments)
        {
            _bound!.Queue(_skeleton, posed ? _posed : _rest, Model, MotionKey.From(123), template);
            return;
        }
        scene.Draw(template with { Mesh = _rigid, World = RigidWorld(posed) });
        if (control == Control.Rigid)
            scene.Draw(template with { Mesh = posed ? _posedGarment : _restGarment });
        else
            scene.DrawSkinned(new SkinnedInstanceDraw(_garmentControl, Model)
            {
                Tint = template.Tint, Dissolve = template.Dissolve, DissolveComplement = template.DissolveComplement,
                InvertShadowDissolve = template.InvertShadowDissolve, ShadowOnly = template.ShadowOnly,
                CastsShadows = template.CastsShadows,
            }, ControlPose(posed));
    }

    internal void MotionFrame(bool gpuSkinning, int step)
    {
        EnsureScene();
        Scene3D scene = _fixture!.Scene;
        Configure(scene, gpuSkinning, 0);
        scene.ForceTemporalForTests = true;
        if (step == 0) scene.CameraCut();
        var pose = new Matrix4x4[2];
        for (int i = 0; i < pose.Length; i++)
            pose[i] = _posed[i] * Matrix4x4.CreateTranslation(.25f * step, .25f * step, 0);
        byte[] pixels = _fixture.Frame((s, _) =>
        {
            s.EffectTimeSeconds = 0;
            _bound!.Queue(_skeleton, pose, Model * Matrix4x4.CreateTranslation(.25f * step, 0, 0),
                MotionKey.From(456), new RigidInstanceDraw(default, Model) { Tint = Red, Dissolve = .5f, DissolveComplement = 1 });
        });
        Evidence($"motion-{gpuSkinning}-{step}", pixels);
    }

    void EnsureScene()
    {
        if (_fixture is not null) return;
        _fixture = new TemporalFixture(W, H, scene => Configure(scene, true, 0));
        Scene3D scene = _fixture.Scene;
        _rest = _skeleton.ComposeRestPose();
        _posed = [Matrix4x4.CreateTranslation(.5f, 1.25f, 0),
            new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, .25f, 3.25f, 0, 1)];
        Matrix4x4[] inverse = [Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.CreateTranslation(0, -3, 0)];
        SkinnedVertex[] hostVertices =
        [Vertex(new(-3.5f, .5f, 0), 0), Vertex(new(-.5f, .5f, 0), 0), Vertex(new(-3.5f, 3.5f, 0), 0)];
        var host = new SkinnedGltfMesh(hostVertices, new ushort[] { 0, 1, 2 }, inverse, _rest, _skeleton);
        _host = scene.LoadSkinnedMesh(host);
        var source = new AttachmentTestSource();
        AttachmentMeshRef garment = source.Mesh("garment.glb", AttachmentGlb(rigid: false));
        AttachmentMeshRef rigid = source.Mesh("rigid.glb", AttachmentGlb(rigid: true));
        var definition = new AttachmentSetDefinition([
            new RigidAttachmentPartDefinition(1, rigid, "hip", RigidLocal),
            new SkinnedAttachmentPartDefinition(2, garment)]);
        _cache = new AttachmentSetCache(scene, source);
        var map = new ContractJointMap(_skeleton, new SkeletonContract([new("hip", null, true), new("upper", "hip", true)]));
        _bound = _cache.Get("mixed.json", source.Add("mixed.json", AttachmentSetJson.Write(definition))).Bind(map, host);
        // The direct control uses HOST bone order, unlike the imported garment's upper, hip order.
        SkinnedVertex[] controlVertices = new SkinnedVertex[3];
        for (int i = 0; i < 3; i++) controlVertices[i] = Vertex(GarmentPositions[i], UpperWeights[i]);
        _garmentControl = scene.LoadSkinnedMesh(new SkinnedGltfMesh(controlVertices, new ushort[] { 0, 1, 2 },
            [Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.CreateTranslation(0, -3, 0)], ControlPose(false)));
        _restGarment = scene.LoadMesh(Triangle(GarmentPositions));
        // Hand-posed vertices: upper = R90(p - (0,3,0)) + (.25,3.25,0),
        // hip = p + (.5,.25,0), blended with upper weights .25, .5 and .75.
        _posedGarment = scene.LoadMesh(Triangle([new(1.4375f, 1.5f, 0), new(3.375f, 3.75f, 0), new(.0625f, 3.75f, 0)]));
        _rigid = scene.LoadMesh(Triangle([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]));
        _floor = scene.LoadMesh(MeshPrimitives.Plane(24, 24));
    }

    static byte[] AttachmentGlb(bool rigid)
    {
        var fixture = new SkinnedAttachmentGlbFixture(2);
        fixture.Nodes[0]!["name"] = "hip";
        fixture.Nodes[0]!["translation"] = new JsonArray(0, 1, 0);
        fixture.Nodes[1]!["name"] = "upper";
        fixture.Nodes[1]!["translation"] = new JsonArray(0, 2, 0);
        fixture.SetSkinJoints(1, 0);
        fixture.SetInverseBind(0, Matrix4x4.CreateTranslation(0, -3, 0));
        fixture.SetInverseBind(1, Matrix4x4.CreateTranslation(0, -1, 0));
        for (int i = 0; i < 3; i++)
        {
            Vector3 p = GarmentPositions[i];
            fixture.SetElement(0, i, p.X, p.Y, p.Z);
            fixture.SetElement(1, i, 0, 1, 0, 0);
            fixture.SetElement(2, i, UpperWeights[i], 1 - UpperWeights[i], 0, 0);
        }
        fixture.Accessors[0]!["min"] = new JsonArray(.5f, .5f, 0);
        fixture.Accessors[0]!["max"] = new JsonArray(3.5f, 3.5f, 0);
        fixture.Root["materials"] = new JsonArray(new JsonObject
        {
            ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(1, 1, 1, 1) },
        });
        fixture.Primitives[0]!["material"] = 0;
        byte[] normals = new byte[36];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(normals.AsSpan(i * 12 + 8), 1);
        int normalAccessor = fixture.Accessors.Count;
        fixture.Accessors.Add(new JsonObject { ["bufferView"] = fixture.AddView(normals),
            ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3" });
        fixture.Primitives[0]!["attributes"]!["NORMAL"] = normalAccessor;
        if (rigid)
        {
            fixture.Root.Remove("skins");
            fixture.MeshNode.Remove("skin");
            JsonObject attributes = fixture.Primitives[0]!["attributes"]!.AsObject();
            attributes.Remove("JOINTS_0");
            attributes.Remove("WEIGHTS_0");
            fixture.SetElement(0, 0, 0, 0, 0);
            fixture.SetElement(0, 1, 1, 0, 0);
            fixture.SetElement(0, 2, 0, 1, 0);
            fixture.Accessors[0]!["min"] = new JsonArray(0, 0, 0);
            fixture.Accessors[0]!["max"] = new JsonArray(1, 1, 0);
        }
        return fixture.Build();
    }

    // Literal transforms, independent of AttachmentSetBinding, its output palettes and the host pose storage.
    static Matrix4x4[] ControlPose(bool posed) => posed
        ? [new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, .5f, 1.25f, 0, 1),
            new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, .25f, 3.25f, 0, 1)]
        : [Matrix4x4.CreateTranslation(0, 1, 0), Matrix4x4.CreateTranslation(0, 3, 0)];

    static Matrix4x4 RigidWorld(bool posed) => Matrix4x4.CreateScale(.75f)
        * Matrix4x4.CreateTranslation(posed ? 7.5f : 7, posed ? 4.25f : 4, 0);

    static SkinnedVertex Vertex(Vector3 position, float upperWeight) => new()
    {
        Position = position, Normal = Vector3.UnitZ, Color = Vector4.One,
        BoneIndices = new Vector4(1, 0, 0, 0), BoneWeights = new Vector4(upperWeight, 1 - upperWeight, 0, 0),
    };

    static GltfMesh Triangle(Vector3[] positions) => new(
        [new ModelVertex(positions[0], Vector3.UnitZ, Vector4.One),
            new ModelVertex(positions[1], Vector3.UnitZ, Vector4.One),
            new ModelVertex(positions[2], Vector3.UnitZ, Vector4.One)],
        new ushort[] { 0, 1, 2 });

    static JointPose At(float x, float y) => new()
    { Translation = new Vector3(x, y, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };

    static void Configure(Scene3D scene, bool gpuSkinning, int light)
    {
        // Views: 0 front colour, 1 key shadow, 2 point shadow, 3 the key receiver without shadow maps.
        scene.UseGpuSkinning = gpuSkinning;
        scene.ForceTemporalForTests = false;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Hdr.Enabled = false;
        scene.Post.CelBands = 0;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Quality.Shadows.Mode = light == 1 ? ShadowMode.ShadowMap : ShadowMode.Off;
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(.4f, -.8f, -.5f));
        scene.Post.LightColor = light == 2 ? Color.Black : Color.White;
        scene.Post.FillLightColor = light == 2 ? Color.Black : new Color(.2f, .2f, .2f, 1);
        scene.Post.AmbientColor = light == 2 ? Color.Black : new Color(.3f, .3f, .3f, 1);
        scene.Post.BackgroundColor = new Color(.02f, .03f, .05f, 1);
        scene.Camera.Azimuth = light == 0 ? 0 : .4f;
        scene.Camera.Elevation = light == 0 ? .001f : .85f;
        scene.Camera.Frame(new Vector3(10, light == 0 ? 2 : 0, 0), light == 0 ? new Vector3(9, 7, 4) : new Vector3(12, 8, 12), 1);
    }

    internal static void Evidence(string name, byte[] pixels, [CallerFilePath] string source = "")
    {
        string directory = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR")
            ?? Path.Combine(Path.GetDirectoryName(source)!, "goldens-evidence");
        Directory.CreateDirectory(directory);
        string backend = GoldenCompare.GoldenBackendToken(GpuBackendSelector.Select());
        string fileName = $"skinned-attachment-{name}.{backend}.png";
        string path = Path.Combine(directory, fileName);
        PngWriter.Save(path, pixels, W, H);
        // The existing always-uploaded text artifact preserves successful proof PNGs without a workflow change.
        File.AppendAllText(Path.Combine(directory, $"golden-deltas.skinned-attachment-pngs.{backend}.txt"),
            fileName + " " + Convert.ToBase64String(File.ReadAllBytes(path)) + Environment.NewLine);
    }

    public void Dispose()
    {
        _fixture?.Device.WaitForIdle();
        _cache?.Dispose();
        _fixture?.Dispose();
    }
}
