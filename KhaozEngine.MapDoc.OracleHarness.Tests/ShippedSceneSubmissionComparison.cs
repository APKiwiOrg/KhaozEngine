using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record SceneExpectedSubmission(MapDrawRecord Draw, MapRigidResource? Rigid = null);

internal static partial class ShippedSceneComparison
{
    // Both source acceptance and seam guards consume this exact keyed matching path.
    internal static void CompareDrawSets(IReadOnlyList<SceneExpectedSubmission> expected, MapDrawSet actual, PrivateOracleReport report)
    {
        var left = expected.GroupBy(d => d.Draw.Resource.Key).ToDictionary(g => g.Key, g => g.ToArray());
        var right = actual.Draws.GroupBy(d => d.Resource.Key).ToDictionary(g => g.Key, g => g.ToList());
        var registrations = actual.Resources.GroupBy(r => r.Reference).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var key in left.Keys.Union(right.Keys))
        {
            var wants = left.GetValueOrDefault(key) ?? [];
            var remaining = right.GetValueOrDefault(key) ?? [];
            Check(report, FrameCategory(key.Kind), wants.Length == remaining.Count, new { key, expected = wants.Length, actual = remaining.Count });
            foreach (var want in wants)
            {
                if (remaining.Count != 0)
                    Check(report, key.Kind == MapRenderKind.Roof ? "scene-roof-channel" : Category(key.Kind),
                        remaining.Any(d => d.Channels == want.Draw.Channels), new { key });
                int match = remaining.FindIndex(d => d.Channels == want.Draw.Channels && d.Anchor == want.Draw.Anchor &&
                    MatrixNear(d.LocalTransform, want.Draw.LocalTransform) && PayloadMatches(want, d));
                Check(report, FrameCategory(key.Kind), match >= 0, new { key });
                if (match < 0) continue;
                var got = remaining[match]; remaining.RemoveAt(match);
                Check(report, "scene-focus", Bits(want.Draw.LocalFocus, got.LocalFocus), new { key });
                Check(report, "scene-rigid-frame", Bits(want.Draw.Rigid, got.Rigid), new { key });
                Check(report, "scene-light-frame", Bits(want.Draw.Light, got.Light), new { key });
                Check(report, "scene-water-frame", Bits(want.Draw.Water, got.Water), new { key });
                Check(report, "scene-foliage-frame", Bits(want.Draw.Foliage, got.Foliage), new { key });
            }
            Check(report, FrameCategory(key.Kind), remaining.Count == 0, new { key, unmatched = remaining.Count });
        }
        bool PayloadMatches(SceneExpectedSubmission want, MapDrawRecord got)
        {
            if (!registrations.TryGetValue(got.Resource, out var found) || found.Length != 1) return false;
            return want.Rigid is null || found[0].Payload is MapRigidResource rigid && MeshEqual(want.Rigid.Mesh, rigid.Mesh) && MapsEqual(want.Rigid.Maps, rigid.Maps);
        }
    }
    static string FrameCategory(MapRenderKind kind) => kind switch
    {
        MapRenderKind.Placement => "scene-placement-frame",
        MapRenderKind.Roof => "scene-roof-channel",
        MapRenderKind.Terrain => "scene-terrain-frame",
        MapRenderKind.Light => "scene-light-frame",
        MapRenderKind.Water => "scene-water-frame",
        MapRenderKind.Foliage => "scene-foliage-frame",
        _ => "scene-marker"
    };
}
