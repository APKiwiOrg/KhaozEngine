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
            CheckResource(report, FrameCategory(key.Kind), wants.Length == remaining.Count, new { key, expected = wants.Length, actual = remaining.Count }, key, "submission-count");
            foreach (var want in wants)
            {
                if (remaining.Count != 0)
                    CheckResource(report, key.Kind == MapRenderKind.Roof ? "scene-roof-channel" : Category(key.Kind),
                        remaining.Any(d => d.Channels == want.Draw.Channels), new { key }, key, "channels");
                int match = remaining.FindIndex(d => d.Channels == want.Draw.Channels && PayloadMatches(want, d));
                CheckResource(report, FrameCategory(key.Kind), match >= 0, new { key }, key, "submission-match");
                if (match < 0) continue;
                var got = remaining[match]; remaining.RemoveAt(match);
                CheckResource(report, "scene-focus", SceneSubmissionGeometry.FocusEqual(want.Draw, got), new { key }, key, "focus");
                CheckResource(report, "scene-rigid-frame", Bits(want.Draw.Rigid, got.Rigid), new { key }, key, "rigid-frame");
                CheckResource(report, "scene-light-frame", SceneSubmissionGeometry.LightEqual(want.Draw, got), new { key }, key, "light-frame");
                CheckResource(report, "scene-water-frame", Bits(want.Draw.Water, got.Water), new { key }, key, "water-frame");
                CheckResource(report, "scene-foliage-frame", Bits(want.Draw.Foliage, got.Foliage), new { key }, key, "foliage-frame");
            }
            CheckResource(report, FrameCategory(key.Kind), remaining.Count == 0, new { key, unmatched = remaining.Count }, key, "unmatched-submissions");
        }
        bool PayloadMatches(SceneExpectedSubmission want, MapDrawRecord got)
        {
            if (!registrations.TryGetValue(got.Resource, out var found) || found.Length != 1) return false;
            if (want.Rigid is null) return SceneSubmissionGeometry.PoseEqual(want.Draw, got);
            if (found[0].Payload is not MapRigidResource rigid || !MapsEqual(want.Rigid.Maps, rigid.Maps)) return false;
            return SceneSubmissionGeometry.IsProxy(want.Draw.Resource.Key)
                ? SceneProxyGeometry.Equal(want.Rigid.Mesh, want.Draw, rigid.Mesh, got)
                : SceneSubmissionGeometry.PoseEqual(want.Draw, got) && MeshEqual(want.Rigid.Mesh, rigid.Mesh);
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
