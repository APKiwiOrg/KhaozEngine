using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

// The shown shape is a solved step behind, carried by the current anchor.
// Compare *changes* in shown travel with both adjacent intervals of input motion.
internal sealed class ChainShownMotionCheck(ChainSimulation simulation)
{
    private readonly Matrix4x4[] _worlds = new Matrix4x4[simulation.Rig.JointCount];
    private readonly Matrix4x4[] _anchors = new Matrix4x4[simulation.Rig.AnchorCount];
    private readonly ColliderCapsule[] _capsules = new ColliderCapsule[simulation.ColliderCount];
    private readonly Vector3[] _before = new Vector3[simulation.Rig.JointCount + simulation.Rig.Chains.Count];
    private readonly Vector3[] _travel = new Vector3[simulation.Rig.JointCount + simulation.Rig.Chains.Count];
    private float _previousDrive, _olderDrive;

    internal static void AssertInitialClearance(ChainSimulation simulation, Matrix4x4[] anchors, ColliderCapsule[] solids)
    {
        var rig = simulation.Rig;
        var worlds = new Matrix4x4[rig.JointCount];
        simulation.ComposeJointWorlds(anchors, worlds);
        Assert.NotEmpty(rig.Links);
        var obstacles = new System.Collections.Generic.List<ColliderCapsule>(solids);
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            if (definition.Layer != AttachmentLayer.Armour)
                continue;

            for (int row = 0; row < definition.Joints.Count; row++)
                obstacles.Add(new(At(chain, row), At(chain, row + 1), definition.Settings.Radius));
        }
        Assert.NotEmpty(obstacles);

        foreach (var link in rig.Links)
        {
            var left = rig.Chains[link.First];
            var right = rig.Chains[link.Second];
            int rows = Math.Min(left.Joints.Count, right.Joints.Count);
            float radius = MathF.Max(left.Settings.Radius, right.Settings.Radius);
            float stretch = link.Stretch.GetValueOrDefault();
            for (int row = 1; row <= rows; row++)
            {
                var a = At(link.First, row - 1);
                var b = At(link.Second, row - 1);
                var c = At(link.First, row == rows ? left.Joints.Count : row);
                var d = At(link.Second, row == rows ? right.Joints.Count : row);
                float rest = Vector3.Distance(Rest(link.First, row == rows ? left.Joints.Count : row),
                    Rest(link.Second, row == rows ? right.Joints.Count : row));
                Assert.InRange(Vector3.Distance(c, d), rest * (1 - stretch), rest * (1 + stretch));
                foreach (var obstacle in obstacles)
                {
                    float clearance = MathF.Min(ChainSheetContactTests.AxisDistance(obstacle, a, c, d),
                        ChainSheetContactTests.AxisDistance(obstacle, a, d, b));
                    Assert.True(clearance >= radius + obstacle.Radius, $"The opening band intersects a capsule at row {row}.");
                }
            }
        }

        Vector3 At(int chain, int row) => Position(chain, row, worlds);
        Vector3 Rest(int chain, int row) => Position(chain, row, rig.RestFromAnchor);

        Vector3 Position(int chain, int row, ReadOnlySpan<Matrix4x4> poses)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            return row == definition.Joints.Count ? Vector3.Transform(definition.Tip, poses[first + row - 1]) :
                poses[first + row].Translation;
        }
    }

    internal void Check(int frame, ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> capsules)
    {
        var rig = simulation.Rig;
        simulation.ComposeJointWorlds(anchors, _worlds);
        float drive = frame == 0 ? 0 : Drive(rig, anchors, capsules);

        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            float seconds = simulation.Options.MaxStepSeconds;
            int first = rig.FirstJoint(chain);
            int last = first + definition.Joints.Count - 1;
            float springTravel = 0;
            for (int joint = first; joint <= last; joint++)
            {
                Observe(joint + chain, _worlds[joint].Translation);
                // The spring can change a point's speed by k² times the rest
                // chord, integrated for one step for each of its upstream links.
                float reach = 2 * rig.SegmentOffset(joint).Length() * MathF.Sin(definition.Settings.AngleLimit / 2);
                springTravel += definition.Settings.Stiffness * definition.Settings.Stiffness * reach * seconds * seconds;
            }
            Observe(last + chain + 1, Vector3.Transform(definition.Tip, _worlds[last]));

            void Observe(int point, Vector3 position)
            {
                var travel = position - _before[point];
                if (frame > 2 && definition.Layer == AttachmentLayer.Outerwear)
                {
                    float velocityChange = Vector3.Distance(travel, _travel[point]);
                    float freeTravel = springTravel + simulation.Options.Gravity.Length() * definition.Settings.Gravity * seconds * seconds +
                        _travel[point].Length() * (1 - MathF.Exp(-definition.Settings.Damping * seconds));
                    // The solved sample is one interval behind the current anchor.
                    // Use the larger of the two source windows that can contribute.
                    float maximum = MathF.Max(drive, _olderDrive) + _previousDrive + freeTravel;
                    Assert.True(velocityChange <= maximum,
                        $"Frame {frame}, point {point}: shown travel changed by {velocityChange} m (motion bound {maximum} m).");
                }
                _before[point] = position;
                _travel[point] = travel;
            }
        }

        anchors.CopyTo(_anchors);
        capsules.CopyTo(_capsules);
        _olderDrive = _previousDrive;
        _previousDrive = drive;
    }

    private float Drive(ChainRig rig, ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> capsules)
    {
        float anchorTravel = 0;
        float obstacleTravel = 0;
        int outerAnchor = rig.Chains[0].Anchor;
        Assert.True(Matrix4x4.Invert(_anchors[outerAnchor], out var inverse));
        var carry = inverse * anchors[outerAnchor];

        for (int capsule = 0; capsule < capsules.Length; capsule++)
        {
            var current = capsules[capsule];
            var previous = _capsules[capsule];
            AddObstacleTravel(current.Start, previous.Start);
            AddObstacleTravel(current.End, previous.End);
        }

        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int last = first + definition.Joints.Count - 1;
            var anchor = anchors[definition.Anchor];
            var previousAnchor = _anchors[definition.Anchor];
            if (definition.Layer == AttachmentLayer.Outerwear)
            {
                Assert.Equal(outerAnchor, definition.Anchor);
                for (int joint = first; joint <= last; joint++)
                    AddAnchorTravel(rig.RestFromAnchor[joint].Translation, anchor, previousAnchor);
                AddAnchorTravel(Vector3.Transform(definition.Tip, rig.RestFromAnchor[last]), anchor, previousAnchor);
            }
            else if (definition.Layer == AttachmentLayer.Armour)
            {
                for (int joint = first; joint <= last; joint++)
                    AddObstacleTravel(_worlds[joint].Translation, _before[joint + chain]);
                AddObstacleTravel(Vector3.Transform(definition.Tip, _worlds[last]), _before[last + chain + 1]);
            }
        }

        // World travel can include both anchor carry and contact movement
        // relative to that carry. They can be in the same direction.
        return anchorTravel + obstacleTravel;

        void AddAnchorTravel(Vector3 rest, Matrix4x4 current, Matrix4x4 previous) =>
            anchorTravel = MathF.Max(anchorTravel, Vector3.Distance(Vector3.Transform(rest, current), Vector3.Transform(rest, previous)));

        void AddObstacleTravel(Vector3 current, Vector3 previous) =>
            obstacleTravel = MathF.Max(obstacleTravel, Vector3.Distance(current, Vector3.Transform(previous, carry)));
    }
}
