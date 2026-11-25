using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed class SwingCostWearer : IDisposable
{
    static readonly RigidInstanceDraw Template = new(default, Matrix4x4.Identity);
    readonly BoundOutfit outfit;
    readonly WornMotion motion;
    readonly Skeleton skeleton;
    readonly JointPose[] locals;
    readonly Matrix4x4[] palette;
    readonly int hip, knee;
    readonly float phase;
    readonly MotionKey key;
    readonly SwingCostPiece[] pieces;
    readonly AttachmentDraw[][] draws;
    MotionLevel level;
    Matrix4x4 model;

    internal SwingCostWearer(OutfitRig rig, LoadedBodyRegions body, SwingCostPiece[] pieces,
        LoadedBodyColliders colliders, ChainSimulationOptions options, int index, int wearerCount)
    {
        skeleton = rig.Skeleton;
        locals = (JointPose[])skeleton.RestLocal.Clone();
        palette = new Matrix4x4[skeleton.BoneCount];
        hip = skeleton.IndexOf("hip");
        knee = skeleton.IndexOf("knee");
        phase = MathF.Tau * index / wearerCount;
        key = MotionKey.From((ulong)(index + 1));
        this.pieces = pieces;
        draws = pieces.Select(_ => new AttachmentDraw[1]).ToArray();

        // Each wearer has independent draw scratch as well as independent motion.
        outfit = rig.Cache.BindOutfit(body, pieces.Select(piece => rig.Bind(piece.Set)).ToArray());
        try
        {
            for (int worn = 0; worn < draws.Length; worn++) Assert.Equal(draws[worn].Length, outfit.DrawCount(worn));
            motion = outfit.CreateMotion(colliders, options);
        }
        catch
        {
            outfit.Dispose();
            throw;
        }
    }

    internal WornMotion Motion => motion;
    internal ReadOnlyMemory<Matrix4x4> Palette => palette;
    internal Matrix4x4 Model => model;
    internal AttachmentDraw[][] Draws => draws;

    internal void Reset(MotionLevel level)
    {
        this.level = level;
        motion.Reset();
    }

    internal void Pose(float seconds)
    {
        float stride = MathF.Tau * 1.8f * seconds + phase;
        locals[hip].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .16f * MathF.Sin(stride));
        locals[knee].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .50f * MathF.Sin(stride + .8f));
        skeleton.ComposeInto(locals, palette);

        // A circle with constant tangential speed of 3 m/s, with local +Z facing along the tangent.
        const float turnRate = .65f;
        const float radius = 3f / turnRate;
        float turn = phase + turnRate * seconds;
        var position = new Vector3(radius * MathF.Cos(turn), 0, radius * MathF.Sin(turn));
        model = Matrix4x4.CreateRotationY(-turn) * Matrix4x4.CreateTranslation(position);
    }

    internal void Step(float frameTime) => motion.Step(skeleton, palette, model, frameTime, level);

    internal void AddTo(WornMotionBatch batch, float frameTime) =>
        batch.Add(motion, skeleton, palette, model, frameTime, level);

    internal void WriteDraws()
    {
        for (int worn = 0; worn < draws.Length; worn++)
            outfit.WriteDraws(worn, motion, skeleton, palette, model, key, Template, draws[worn]);
    }

    internal void ValidateDraws()
    {
        for (int worn = 0; worn < draws.Length; worn++)
        {
            AttachmentDraw draw = draws[worn][0];
            Assert.Equal(1u, draw.PartId);
            Assert.Equal(pieces[worn].PaletteBones.HasValue, draw.IsSkinned);
            if (pieces[worn].PaletteBones is int joints)
            {
                ValidateMatrix(draw.Skinned.Model);
                ReadOnlySpan<Matrix4x4> palette = draw.BoneMatrices.Span;
                Assert.Equal(joints, palette.Length);
                foreach (Matrix4x4 pose in palette) ValidateMatrix(pose);
            }
            else
            {
                ValidateMatrix(draw.Rigid.World);
            }
        }
    }

    static void ValidateMatrix(in Matrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                Assert.True(float.IsFinite(matrix[row, column]), "A draw matrix is non-finite.");
    }

    public void Dispose() => outfit.Dispose();
}
