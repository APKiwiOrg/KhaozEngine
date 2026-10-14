using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Five independent joints with reordered skin indices, a baked node offset and two material groups.</summary>
public sealed class RigidAttachmentGpuScene : IDisposable
{
    internal const int W = 480, H = 320;
    static readonly Vector4 Red = new(.8f, .1f, .1f, 1);
    static readonly Vector4 Green = new(.1f, .8f, .2f, 1);
    static readonly Matrix4x4 QuarterTurn = new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    static readonly Matrix4x4 ReverseQuarterTurn = new(0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    static readonly Matrix4x4 HalfTurn = new(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    readonly Skeleton _skeleton = new([-1, 0, 0, 0, 0, 0],
        [JointPose.Identity, At(0, 2), At(-2, 1), At(2, 1), At(-1, -1), At(1, -1)],
        [0, 1, 2, 3, 4, 5], [5, 3, 1, 4, 2], ["root", "a", "b", "c", "d", "e"]);
    TemporalFixture? _fixture;
    AttachmentSetCache? _cache;
    BoundAttachmentSet? _bound, _replacement;
    Matrix4x4[]? _rest, _posed;
    MeshHandle _red, _green, _floor;

    internal TemporalFixture Fixture { get { EnsureScene(); return _fixture!; } }

    internal byte[] Capture(bool control, bool posed, bool shadows = false, bool castsShadows = true,
        bool outlines = false)
    {
        EnsureScene();
        Configure(_fixture!.Scene, shadows);
        Matrix4x4[] palette = posed ? _posed! : _rest!;
        return _fixture.Frame((scene, _) =>
        {
            if (shadows) scene.Draw(_floor, Matrix4x4.CreateTranslation(10, -2, 0), new Color(.65f, .65f, .65f, 1));
            if (control)
            {
                foreach (Matrix4x4 world in ControlWorlds(posed))
                    foreach (MeshHandle mesh in new[] { _red, _green })
                        scene.Draw(new RigidInstanceDraw(mesh, world) { CastsShadows = castsShadows });
            }
            else
            {
                var template = new RigidInstanceDraw(default, Matrix4x4.Identity) { CastsShadows = castsShadows };
                _bound!.Queue(_skeleton, palette, Matrix4x4.CreateTranslation(10, 0, 0), MotionKey.From(123), template);
            }
            if (!outlines) return;
            MeshOutlineGroup group = scene.BeginMeshOutline(new Color(.1f, .3f, 1, 1), 2);
            if (control)
                foreach (Matrix4x4 world in ControlWorlds(posed))
                {
                    scene.DrawMeshOutline(group, _red, world);
                    scene.DrawMeshOutline(group, _green, world);
                }
            else
            {
                var draws = new RigidInstanceDraw[_bound!.DrawCount];
                _bound.WriteDraws(_skeleton, palette, Matrix4x4.CreateTranslation(10, 0, 0), MotionKey.None,
                    new RigidInstanceDraw(default, Matrix4x4.Identity), draws);
                foreach (RigidInstanceDraw draw in draws) scene.DrawMeshOutline(group, draw.Mesh, draw.World);
            }
        });
    }

    internal byte[] MotionFrame(float modelX, bool replacement, MotionKey key)
    {
        EnsureScene();
        Configure(_fixture!.Scene, shadows: false);
        _fixture.Scene.ForceTemporalForTests = true;
        return _fixture.Frame((_, _) => (replacement ? _replacement! : _bound!).Queue(
            _skeleton, _rest!, Matrix4x4.CreateTranslation(modelX, 0, 0), key));
    }

    internal static void Evidence(string name, byte[] rgba)
    {
        string? directory = Environment.GetEnvironmentVariable("KE_ATTACHMENT_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        PngWriter.Save(Path.Combine(directory, name + ".png"), rgba, W, H);
    }

    internal static Matrix4x4[] ControlWorlds(bool posed) => posed
        ? [QuarterTurn * Matrix4x4.CreateTranslation(10, 2.5f, 0),
            ReverseQuarterTurn * Matrix4x4.CreateTranslation(8, .5f, 0),
            HalfTurn * Matrix4x4.CreateTranslation(11.5f, 1, 0),
            Matrix4x4.CreateTranslation(9.5f, -.5f, 0), Matrix4x4.CreateTranslation(11.5f, -1.5f, 0)]
        : [Matrix4x4.CreateTranslation(10.5f, 2, 0), Matrix4x4.CreateTranslation(8.5f, 1, 0),
            Matrix4x4.CreateTranslation(12.5f, 1, 0), Matrix4x4.CreateTranslation(9.5f, -1, 0),
            Matrix4x4.CreateTranslation(11.5f, -1, 0)];

    void EnsureScene()
    {
        if (_fixture is not null) return;
        _fixture = new TemporalFixture(W, H, scene => Configure(scene, shadows: false));
        var source = new AttachmentTestSource();
        _cache = new AttachmentSetCache(_fixture.Scene, source);
        var map = new ContractJointMap(_skeleton, new SkeletonContract(
            [new("root", null, false), new("a", "root", true), new("b", "root", true),
                new("c", "root", true), new("d", "root", true), new("e", "root", true)]));
        BoundAttachmentSet Load(string label)
        {
            AttachmentMeshRef mesh = source.Mesh("part.glb", Glb(label));
            var definition = new AttachmentSetDefinition(new[] { "a", "b", "c", "d", "e" }
                .Select((joint, i) => new RigidAttachmentPartDefinition((uint)i + 1, mesh, joint,
                    Matrix4x4.CreateTranslation(.5f, 0, 0))));
            string key = label + ".json";
            return _cache.Get(key, source.Add(key, AttachmentSetJson.Write(definition))).Bind(map);
        }
        _bound = Load("original");
        _replacement = Load("replacement");
        _rest = _skeleton.ComposeRestPose();
        // Skin order is e, c, a, d, b. These are explicit posed joint-world matrices.
        _posed = [Matrix4x4.CreateTranslation(1, -1.5f, 0), HalfTurn * Matrix4x4.CreateTranslation(2, 1, 0),
            QuarterTurn * Matrix4x4.CreateTranslation(0, 2, 0), Matrix4x4.CreateTranslation(-1, -.5f, 0),
            ReverseQuarterTurn * Matrix4x4.CreateTranslation(-2, 1, 0)];
        // Literal geometry includes the authored node's (.25, .5, 0) offset independently of the importer.
        _red = _fixture.Scene.LoadMesh(ControlQuad(-.15f, .25f, Red));
        _green = _fixture.Scene.LoadMesh(ControlQuad(.25f, .65f, Green));
        _floor = _fixture.Scene.LoadMesh(MeshPrimitives.Plane(16, 16));
    }

    static void Configure(Scene3D scene, bool shadows)
    {
        scene.ForceTemporalForTests = false;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Hdr.Enabled = false;
        scene.Post.CelBands = 0;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Quality.Shadows.Mode = shadows ? ShadowMode.ShadowMap : ShadowMode.Off;
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(.4f, -.8f, -.5f));
        scene.Post.BackgroundColor = new Color(.02f, .03f, .05f, 1);
        scene.Camera.Azimuth = shadows ? .3f : 0;
        scene.Camera.Elevation = shadows ? .65f : .001f;
        scene.Camera.Frame(new Vector3(10, .5f, 0), new Vector3(8, 7, 4), 1);
    }

    static JointPose At(float x, float y) => new()
    { Translation = new Vector3(x, y, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };

    static GltfMesh ControlQuad(float left, float right, Vector4 color) => new(
        [new ModelVertex(new(left, .15f, 0), Vector3.UnitZ, color),
            new ModelVertex(new(right, .15f, 0), Vector3.UnitZ, color),
            new ModelVertex(new(right, .85f, 0), Vector3.UnitZ, color),
            new ModelVertex(new(left, .85f, 0), Vector3.UnitZ, color)],
        new ushort[] { 0, 1, 2, 0, 2, 3 });

    static byte[] Glb(string label)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>(label);
        void Add(float left, float right, Vector4 color, string name)
        {
            var primitive = mesh.UsePrimitive(new MaterialBuilder(name).WithBaseColor(color));
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(float x, float y) =>
                new(new VertexPositionNormal(new Vector3(x, y, 0), Vector3.UnitZ));
            primitive.AddTriangle(V(left, -.35f), V(right, -.35f), V(right, .35f));
            primitive.AddTriangle(V(left, -.35f), V(right, .35f), V(left, .35f));
        }
        Add(-.4f, 0, Red, "red");
        Add(0, .4f, Green, "green");
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.CreateTranslation(.25f, .5f, 0));
        string path = Path.Combine(Path.GetTempPath(), $"attachment-gpu-{Guid.NewGuid():N}.glb");
        try { scene.ToGltf2().SaveGLB(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }

    public void Dispose()
    {
        _fixture?.Device.WaitForIdle();
        _cache?.Dispose();
        _fixture?.Dispose();
    }
}
