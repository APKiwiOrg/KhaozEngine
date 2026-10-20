using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using Proof = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    public static IEnumerable<object[]> LyingLogCrestRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (float radius in new[] { 0.1f, 0.15f })
                foreach (bool aboutX in new[] { false, true })
                    yield return [capsule, radius, aboutX];
    }

    [Theory]
    [MemberData(nameof(LyingLogCrestRows))]
    public void LyingLogCrestRegionSupportsWithInstalledPoseOracle(bool capsule, float radius, bool aboutX)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
        StaticHandle owner = InstallLyingLog(scene, capsule, radius,
            Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, MathF.PI / 2));
        using var lease = scene.World.AcquireQueryReadLease();
        var failures = new List<string>();
        for (int step = -12; step <= 12; step++)
        {
            float across = step / 60f;
            // Feet on the crest, as a body that has climbed the flank queries its next step.
            var query = new FootSupportQuery(aboutX ? new(across, 0) : new(0, across), 2 * radius,
                0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
            var request = FootSupport.DescribeFootprint(query);
            var expected = InstalledLyingLogSelect(scene, owner, request);
            // The binary64 locator selects the stratum. Its rational certificate encloses the published height.
            B? exact = expected.Static == owner ? InstalledLyingLogContribution(scene, owner, request, expected) : null;
            // At |across| 0.2 the disc rim is tangent to the crest line. That boundary row
            // asserts containment, and every interior row also keeps the half-skin ceiling.
            float ceiling = Math.Abs(step) == 12 ? float.PositiveInfinity : 0.0005f;
            foreach (bool reference in new[] { false, true })
            {
                using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
                var sample = FootSupport.Find(null, scene.World, lease, query);
                if (expected.Status == SupportStatus.Walkable && expected.Static == owner &&
                    sample.Status == SupportStatus.Walkable && sample.Static == owner &&
                    sample.FeatureId == expected.ElementId && sample.HeightError >= 0f && sample.HeightError <= ceiling &&
                    exact is { } height && R.From(sample.Height) - R.From(sample.HeightError) <= height.Lower &&
                    R.From(sample.Height) + R.From(sample.HeightError) >= height.Upper) continue;
                failures.Add($"across {across:R} reference {reference}: {sample.Status} h {sample.Height:R} " +
                    $"+- {sample.HeightError:R}, oracle {expected.Status} {expected.Static?.Value}/{expected.ElementId} " +
                    $"[{expected.HeightLower:R}, {expected.HeightUpper:R}] exact " +
                    (exact is { } e ? $"[{SupportFootprintExactOracle.AsDouble(e.Lower):R}, {SupportFootprintExactOracle.AsDouble(e.Upper):R}]" : "none"));
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // The selected witness's rational enclosure and its tangent plane at the query axis. Near a flat crest the
    // binary64 locator's own plane misses the exact attained point by more than one unit in the last place.
    static B InstalledLyingLogContribution(SupportFootprintScene scene, StaticHandle owner,
        SupportFootprintRequest request, FootprintOracleSupport expected)
    {
        var part = Proof.Installed(scene.World, owner).Single(p => p.Tangent.ElementId == expected.ElementId);
        var locator = SupportFootprintOracle.Curved(part.Tangent, request)!.Witnesses.First(w => w.Position == expected.Witness);
        var certificate = SupportFootprintTiltedOracle.Enclose(part, locator, request);
        P position = certificate.Position, normal = certificate.Normal;
        B plane = position.Y + (normal.X * (position.X - (B)R.From(request.Axis.X)) +
            normal.Z * (position.Z - (B)R.From(request.Axis.Y))) / normal.Y;
        return new(position.Y.Lower < plane.Lower ? position.Y.Lower : plane.Lower,
            position.Y.Upper < plane.Upper ? position.Y.Upper : plane.Upper);
    }

    // The log's parts come from its installed float pose, including the composed child centre,
    // rather than a double recomposition of the authored pose.
    static FootprintOracleSupport InstalledLyingLogSelect(SupportFootprintScene scene, StaticHandle owner,
        SupportFootprintRequest request)
    {
        var members = Members(scene, request).Where(m => m.Static != owner).ToList();
        foreach (var part in Proof.Installed(scene.World, owner))
            if (SupportFootprintOracle.Curved(part.Tangent, request) is { } member) members.Add(member);
        return SupportFootprintOracle.Select(members.OrderBy(m => m.Static.Value).ThenBy(m => m.ElementId).ToList(),
            request);
    }
}
