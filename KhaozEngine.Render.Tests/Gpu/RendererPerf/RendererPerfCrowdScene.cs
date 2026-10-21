using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Distinct public tubes with current joint palettes, stable motion keys and two point-shadow rows.</summary>
internal sealed class RendererPerfCrowdScene : RendererPerfVillageScene
{
    readonly SkinnedGltfMesh _tube = StreamedWorldSceneContent.CharacterTube();
    readonly Matrix4x4[] _worlds;
    readonly Matrix4x4[][] _poses;
    readonly string[] _poseDigests;
    SkinnedMeshHandle[] _handles = [];
    static readonly Vector3 StaticLight = new(-5f, 3f, 0f), DynamicLight = new(5f, 3f, 0f);
    internal RendererPerfCrowdScene() : base("crowd", 4, 128, 0)
    {
        _worlds = Enumerable.Range(0, 32).Select(i => Matrix4x4.CreateTranslation(
            (i % 8 - 3.5f) * 3.5f, 1f, (i / 8 - 1.5f) * 3.5f)).ToArray();
        _poses = Enumerable.Range(0, Period).Select(frame =>
        {
            // The shared fixture returns model-space deformations. DrawSkinned accepts joint worlds,
            // so compose each deformation with its rest joint before the engine applies inverse-bind.
            Matrix4x4[] deformations = SkinnedPointShadowScene.BentPose(_tube, .1f + Phase(frame) * .25f);
            for (int bone = 0; bone < deformations.Length; bone++)
                deformations[bone] = _tube.RestPose[bone] * deformations[bone];
            return deformations;
        }).ToArray();
        _poseDigests = _poses.Select(BufferDigest).ToArray();
    }
    protected override int Actors => 32;
    protected override string PoseDigest(int frame) => _poseDigests[frame];
    protected override object Geometry => new
    {
        Rigid = base.Geometry,
        Actor = new
        {
            Vertices = _tube.Vertices.Length,
            Indices = _tube.Indices32.Length,
            _tube.IndexFormat,
            VertexStride = SkinnedVertex.SizeInBytes,
            VertexBytes = _tube.Vertices.Length * SkinnedVertex.SizeInBytes,
            IndexBytes = _tube.Indices32.Length * 2,
            PaletteWindowBones = 128,
            PaletteSlotBytes = 8192,
            SharedPaletteBytes = 32 * 8192,
            VertexSha256 = BufferDigest(_tube.Vertices),
            IndexSha256 = BufferDigest(_tube.Indices32),
            Bones = _tube.BoneCount,
            _tube.RestPose,
            _tube.InverseBind,
            RestPoseSha256 = BufferDigest(_tube.RestPose),
            InverseBindSha256 = BufferDigest(_tube.InverseBind)
        },
        ActorWorlds = _worlds,
        MotionKeys = Enumerable.Range(1, 32).ToArray(),
        PaletteAtZero = _poses[0],
        ActorTint = Color.White,
        ActorMaterial = Material.None,
        Points = new[] { new { Position = StaticLight, Radius = 32f, Intensity = 3f, Kind = "static", Key = 1 },
            new { Position = DynamicLight, Radius = 32f, Intensity = 3f, Kind = "dynamic", Key = 0 } },
    };
    protected override void Load(Scene3D scene)
    {
        base.Load(scene);
        _handles = Enumerable.Range(0, Actors).Select(_ => scene.LoadSkinnedMesh(_tube)).ToArray();
    }
    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        base.QueueContent(scene, frameIndex);
        for (int actor = 0; actor < Actors; actor++)
            scene.DrawSkinned(new SkinnedInstanceDraw(_handles[actor], _worlds[actor])
            {
                Motion = MotionKey.From((ulong)actor + 1),
                Tint = Color.White,
                Material = Material.None,
                CastsShadows = true,
                ShadowOnly = false,
            }, _poses[frameIndex]);
        scene.AddLight(StaticLight, Color.White, 32f, 3f, LightShadow.Static(1));
        scene.AddLight(DynamicLight, Color.White, 32f, 3f, LightShadow.Dynamic);
    }
}
