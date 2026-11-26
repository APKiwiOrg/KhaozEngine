using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Same constraints as a step, with no integration or writes to the live positions and velocities.
internal sealed class ChainRestPlacement(ChainParticles particles, ChainSolveTiers tiers, int colliderCount)
{
    private readonly Vector3[] _positions = new Vector3[particles.Positions.Length];
    private readonly ColliderCapsule[] _obstacles = new ColliderCapsule[tiers.ObstacleCount];
    private readonly ColliderCapsule[] _openingObstacles = new ColliderCapsule[tiers.ObstacleCount];
    private readonly Matrix4x4[] _fitAnchors = new Matrix4x4[particles.Rig.AnchorCount];
    private readonly ColliderCapsule[] _fitColliders = new ColliderCapsule[colliderCount];
    private readonly Matrix4x4[] _openingFrames = new Matrix4x4[particles.Rig.JointCount];
    private readonly ChainSettings[] _shownSettings = new ChainSettings[tiers.ObstacleCount > colliderCount
        ? particles.Rig.Chains.Count : 0];
    private bool _fitValid;

    internal ChainPresentationFrame Fit { get; } = new(particles.Rig);

    internal void Invalidate() => _fitValid = false;

    internal ReadOnlySpan<ChainSettings> OpeningSettings(ReadOnlySpan<ChainSettings> current) =>
        _shownSettings.Length == 0 ? current : _shownSettings;

    internal void RememberSettings(ReadOnlySpan<ChainSettings> settings)
    {
        if (_shownSettings.Length > 0)
            settings.CopyTo(_shownSettings);
    }

    internal bool UpdateFit(ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ChainPose> poses,
        ReadOnlySpan<ChainPresentationAnchor> frame, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ChainSettings> settings, ReadOnlySpan<ChainPose> openingPoses = default,
        ReadOnlySpan<Matrix4x4> openingFrames = default, ReadOnlySpan<ColliderCapsule> previousColliders = default)
    {
        if (_fitValid && anchors.SequenceEqual(_fitAnchors) && colliders.SequenceEqual(_fitColliders))
            return false;

        Fit.SetRest(frame);
        // An animated obstacle can cross the authored hang. Start the contact sweep from the last shown side.
        bool hasOpening = !openingFrames.IsEmpty;
        Place(anchors, poses, frame, colliders, hasOpening ? previousColliders : colliders, settings,
            hasOpening ? OpeningSettings(settings) : settings, Fit, hasOpening ? openingPoses : poses,
            hasOpening ? openingFrames : Fit.Frames);
        anchors.CopyTo(_fitAnchors);
        colliders.CopyTo(_fitColliders);
        _fitValid = true;
        return true;
    }

    internal void BeginBlend(ChainPresentationFrame shown) => shown.Frames.CopyTo(_openingFrames, 0);

    internal void ProjectBlend(ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ChainPose> poses,
        ReadOnlySpan<ChainPresentationAnchor> frame, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings,
        ChainPresentationFrame shown, ReadOnlySpan<ChainPose> previousPoses) =>
        Place(anchors, poses, frame, colliders, previousColliders, settings, OpeningSettings(settings),
            shown, previousPoses, _openingFrames);

    private void Place(ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ChainPose> poses,
        ReadOnlySpan<ChainPresentationAnchor> frame, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings,
        ReadOnlySpan<ChainSettings> openingSettings, ChainPresentationFrame shown,
        ReadOnlySpan<ChainPose> openingPoses, ReadOnlySpan<Matrix4x4> openingFrames)
    {
        var live = particles.ExchangePositions(_positions);
        try
        {
            particles.SetRest(anchors, reset: false);
            particles.PlaceFromShown(openingPoses, openingFrames);
            particles.SnapshotOpening();
            particles.PlaceFromShown(poses, shown.Frames);
            colliders.CopyTo(_obstacles);
            previousColliders.CopyTo(_openingObstacles);
            for (int i = 0; i < colliders.Length; i++)
                particles.SetCapsuleGeometry(i, colliders[i], previousColliders[i]);
            tiers.SeedOpeningRadii(particles.Rig, _openingObstacles, openingSettings);
            tiers.Solve(particles, anchors, _obstacles, _openingObstacles, settings, Vector3.Zero, 0,
                Vector3.Zero, Vector3.Zero, Quaternion.Identity);
            frame.CopyTo(shown.Anchors);
            shown.CaptureJoints(particles);
        }
        finally
        {
            particles.ExchangePositions(live);
        }
    }
}
