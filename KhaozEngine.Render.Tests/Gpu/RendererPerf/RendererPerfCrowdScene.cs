using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Distinct public tubes with current joint palettes, stable motion keys and two point-shadow rows.</summary>
internal sealed class RendererPerfCrowdScene : RendererPerfVillageScene
{
    readonly SkinnedGltfMesh _tube = RendererPerfTubeInputs.Character.Build();
    readonly Matrix4x4[] _worlds;
    readonly Matrix4x4[][] _poses;
    readonly float[] _poseAngles = Enumerable.Range(0, Period).Select(frame => .1f + Phase(frame) * .25f).ToArray();
    readonly string[] _poseDigests;
    readonly SkinnedInstanceDraw[] _actorDraws;
    internal static readonly SkinnedInstanceDraw DefaultActorPolicy = new(default, Matrix4x4.Identity)
    {
        Tint = Color.White,
        Material = new Material(Color.Transparent, 0f, 32f),
        CastsShadows = true,
        ShadowOnly = false,
        Motion = MotionKey.From(0),
        Dissolve = 0f,
        DissolveEdgeWidth = 0f,
        DissolveEdgeColor = Color.Transparent,
        InvertShadowDissolve = false,
        DissolveComplement = 0f,
    };
    SkinnedMeshHandle[] _handles = [];
    readonly PointConstruction[] _points =
    [
        new(new Vector3(-5f, 3f, 0f), Color.White, 32f, 3f, new LightShadow(LightShadowMode.Static, 1)
            { NearRadius = 0f, ExclusionMin = Vector3.Zero, ExclusionMax = Vector3.Zero }),
        new(new Vector3(5f, 3f, 0f), Color.White, 32f, 3f, new LightShadow(LightShadowMode.Dynamic, 0)
            { NearRadius = 0f, ExclusionMin = Vector3.Zero, ExclusionMax = Vector3.Zero }),
    ];
    internal RendererPerfCrowdScene(SkinnedInstanceDraw? actorPolicy = null) : base("crowd", 4, 128, 0)
    {
        _worlds = Enumerable.Range(0, 32).Select(i => Matrix4x4.CreateTranslation(
            (i % 8 - 3.5f) * 3.5f, 1f, (i / 8 - 1.5f) * 3.5f)).ToArray();
        _actorDraws = _worlds.Select((world, i) => (actorPolicy ?? DefaultActorPolicy) with
        {
            Mesh = new SkinnedMeshHandle(i),
            Model = world,
            Motion = MotionKey.From((ulong)i + 1),
        }).ToArray();
        _poses = Enumerable.Range(0, Period).Select(frame =>
        {
            // The shared fixture returns model-space deformations. DrawSkinned accepts joint worlds,
            // so compose each deformation with its rest joint before the engine applies inverse-bind.
            Matrix4x4[] deformations = SkinnedPointShadowScene.BentPose(_tube, _poseAngles[frame]);
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
            Inputs = RendererPerfTubeInputs.Character,
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
        PoseAngles = _poseAngles,
        ActorDraws = _actorDraws,
        Points = _points,
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
            scene.DrawSkinned(_actorDraws[actor] with { Mesh = _handles[actor] }, _poses[frameIndex]);
        foreach (PointConstruction point in _points)
            scene.AddLight(point.Position, point.Color, point.Radius, point.Intensity, point.Shadow);
    }

    sealed record PointConstruction(Vector3 Position, Color Color, float Radius, float Intensity, LightShadow Shadow);
}
