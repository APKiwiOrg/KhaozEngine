using System;
using System.Numerics;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Where a headless render streams its throwaway <see cref="ViewportWorld"/> and how far. The world draws
/// authored placements and scatter only inside the gameplay ring around its streaming focus, so the focus and the
/// ring decide what a render can show.
/// <para><see cref="ForView"/> streams around the eye with the editor's default ring, so a perspective render sees
/// what the live editor would see from that eye. <see cref="ForTopDown"/> streams around the rect centre with a
/// gameplay ring, prop cull and companion cull wide enough to cover the whole rect, capped at
/// <see cref="MaxCoverChunks"/> so a huge document cannot demand an unbounded ring.</para></summary>
internal readonly record struct RenderStreamPlan(
    Vector3 Focus, RenderDistanceProfile RenderDistance, float CompanionDrawRadius, bool Capped)
{
    /// <summary>The widest gameplay ring a top-down render asks for, in chunks. At 60 m chunks that is a 1440 m
    /// ring, which fully covers a square rect up to about 1.9 km on a side once the chunk-edge slack is taken. A
    /// wider rect draws placements and scatter only inside that reach.</summary>
    internal const int MaxCoverChunks = 24;

    const float ChunkMeters = RenderDistanceProfile.ChunkMeters;

    // A point r metres from the focus sits in a chunk at most r / chunk + sqrt(2) chunk steps from the focus chunk,
    // which is the distance the streamer's gameplay test measures. Rounded up with a little slack.
    const float ChunkReachSlack = 1.5f;

    /// <summary>The distance from the rect centre a capped top-down render still fully covers: the widest ring
    /// less the chunk-edge slack, 1350 m.</summary>
    internal const float MaxCoveredReach = (MaxCoverChunks - ChunkReachSlack) * ChunkMeters;

    /// <summary>A perspective render: stream around <paramref name="eye"/> with the default profile.</summary>
    internal static RenderStreamPlan ForView(Vector3 eye) =>
        new(eye, RenderDistanceProfile.Default, ViewportWorld.DefaultCompanionDrawRadius, Capped: false);

    /// <summary>A top-down render of the rect: stream around <paramref name="focus"/> (the rect centre) with a ring
    /// that reaches every corner, up to <see cref="MaxCoverChunks"/>. A rect the default profile already covers keeps
    /// the default profile. The companion cull always widens to the covered reach, since its default is a 60 m
    /// near field.</summary>
    internal static RenderStreamPlan ForTopDown(Vector3 focus, float minX, float minZ, float maxX, float maxZ)
    {
        float halfWidth = MathF.Abs(maxX - minX) * 0.5f;
        float halfDepth = MathF.Abs(maxZ - minZ) * 0.5f;
        float reach = MathF.Sqrt(halfWidth * halfWidth + halfDepth * halfDepth);
        RenderDistanceProfile standard = RenderDistanceProfile.Default;
        int needed = (int)MathF.Ceiling(reach / ChunkMeters + ChunkReachSlack);
        int gameplay = Math.Clamp(needed, standard.GameplayLoadRadiusChunks, MaxCoverChunks);
        float covered = MathF.Min(reach, gameplay * ChunkMeters);
        float companions = MathF.Max(ViewportWorld.DefaultCompanionDrawRadius, covered);
        // needed <= 4 already bounds reach to (4 - 1.5) * 60 = 150 m, well inside the default 500 m prop cull.
        if (needed <= standard.GameplayLoadRadiusChunks)
            return new(focus, standard, companions, Capped: false);

        float cull = MathF.Max(standard.PropDrawRadius, covered);
        float ocean = cull + ChunkMeters;
        int decor = Math.Max(gameplay + 1, (int)MathF.Ceiling(ocean / ChunkMeters));
        var profile = new RenderDistanceProfile(
            GameplayLoadRadiusChunks: gameplay,
            DecorRadiusChunks: decor,
            UnloadRadiusChunks: decor + 2,
            PropDrawRadius: cull,
            FarClip: cull,
            OceanHalfExtent: ocean);
        profile.Validate(nameof(RenderDistance));
        return new(focus, profile, companions, Capped: needed > MaxCoverChunks);
    }
}
