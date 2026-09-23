using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    internal sealed class InspectionFixture
    {
        public const int RootNode = 0;
        public const int HipsNode = 1;
        public const int LeftThighNode = 2;
        public const int LeftFootNode = 3;
        public const int RightThighNode = 4;
        public const int RightFootNode = 5;
        public const int RightHandNode = 6;
        public const int WeaponSocketNode = 7;

        public Skeleton Skeleton { get; }
        public AnimationClip WalkClip { get; }

        public InspectionFixture()
        {
            Skeleton = CreateSkeleton();
            WalkClip = CreateWalkClip();
        }

        public AnimationClip CreateEndpointClip() =>
            new(
                "endpoint",
                2f,
                new List<JointTrack>
                {
                    new(101)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f, 2f },
                            new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 2f) },
                            InterpolationMode.Linear),
                    },
                });

        static Skeleton CreateSkeleton() =>
            new(
                new[] { -1, 0, 1, 2, 1, 4, 1, 6 },
                new[]
                {
                    JointPose.Identity,
                    Pose(new Vector3(0f, 1f, 0f)),
                    Pose(new Vector3(-0.25f, -0.5f, 0f)),
                    Pose(new Vector3(0f, -0.5f, 0.15f)),
                    Pose(new Vector3(0.25f, -0.5f, 0f)),
                    Pose(new Vector3(0f, -0.5f, 0.15f)),
                    Pose(new Vector3(0.5f, 0.25f, 0.1f)),
                    Pose(new Vector3(0.1f, 0.2f, 0.4f)),
                },
                new[] { 100, 101, 102, 103, 104, 105, 106, 107 },
                new[] { HipsNode, LeftThighNode, LeftFootNode, RightThighNode, RightFootNode, RightHandNode },
                new[] { "Root", "Hips", "Thigh.L", "Foot.L", "Thigh.R", "Foot.R", "Hand.R", "weapon_socket" });

        static AnimationClip CreateWalkClip()
        {
            var leftFoot = new JointTrack(103)
            {
                Translation = new Vector3Track(
                    new[] { 0f, 0.5f, 1f, 1.5f, 2f },
                    new[]
                    {
                        new Vector3(0f, -0.5f, 0.65f),
                        new Vector3(0f, -0.5f, 0.15f),
                        new Vector3(0f, -0.5f, -0.35f),
                        new Vector3(0f, -0.2f, 0.15f),
                        new Vector3(0f, -0.5f, 0.65f),
                    },
                    InterpolationMode.Linear),
            };
            var rightFoot = new JointTrack(105)
            {
                Translation = new Vector3Track(
                    new[] { 0f, 0.5f, 1f, 1.5f, 2f },
                    new[]
                    {
                        new Vector3(0f, -0.5f, -0.35f),
                        new Vector3(0f, -0.2f, 0.15f),
                        new Vector3(0f, -0.5f, 0.65f),
                        new Vector3(0f, -0.5f, 0.15f),
                        new Vector3(0f, -0.5f, -0.35f),
                    },
                    InterpolationMode.Linear),
            };
            var rightHand = new JointTrack(106)
            {
                Translation = new Vector3Track(
                    new[] { 0f, 1f, 2f },
                    new[]
                    {
                        new Vector3(0.5f, 0.25f, 0.1f),
                        new Vector3(0.5f, 0.45f, 0.3f),
                        new Vector3(0.5f, 0.25f, 0.1f),
                    },
                    InterpolationMode.Linear),
                Rotation = new QuaternionTrack(
                    new[] { 0f, 1f, 2f },
                    new[]
                    {
                        Quaternion.Identity,
                        Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f),
                        Quaternion.Identity,
                    },
                    InterpolationMode.Linear),
            };

            return new AnimationClip("walk", 2f, new List<JointTrack> { leftFoot, rightFoot, rightHand });
        }

        static JointPose Pose(Vector3 translation) =>
            new() { Translation = translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
    }
}
