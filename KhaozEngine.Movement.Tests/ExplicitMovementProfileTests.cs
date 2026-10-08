using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ExplicitMovementProfileTests
{
    static MoveTuning Tuning => MoveTuning.Default with { CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f, WalkSpeed = 4, SwimSpeed = 3 };
    static NavBakeSources Sources(bool reverse = false) => reverse
        ? new NavBakeSources().AddHashOf("cost-policy", new byte[] { 2 }).AddHashOf("query-policy", new byte[] { 1 })
        : new NavBakeSources().AddHashOf("query-policy", new byte[] { 1 }).AddHashOf("cost-policy", new byte[] { 2 });
    static ExplicitMovementProfile Profile(MoveTuning? tuning = null, WaterTraversalPolicy? water = null,
        NavBakeSources? sources = null, NavAreaFilter areas = default, float dt = 1f / 30, int steps = 64, string boundary = "") =>
        new("fixture", tuning ?? Tuning, water ?? new(WaterTraversalMode.SurfaceSwimmer, 7), sources ?? Sources(), areas, dt, steps, boundary);

    [Fact]
    public void SemanticSourcesAreCanonicalAndSnapshotAtProfileConstruction()
    {
        var sources = Sources();
        var profile = Profile(sources: sources);
        string original = profile.Fingerprint;
        Assert.Equal(original, Profile(sources: Sources(reverse: true)).Fingerprint);
        sources.AddHashOf("new-policy", new byte[] { 3 });
        Assert.Equal(original, profile.Fingerprint);
        Assert.NotEqual(original, Profile(sources: sources).Fingerprint);
    }

    public static IEnumerable<object[]> TuningFields => typeof(MoveTuning).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.PropertyType == typeof(float) || p.PropertyType == typeof(bool)).Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(TuningFields))]
    public void EveryCarriedMovementParameterParticipatesInTheIdentity(string name)
    {
        PropertyInfo property = typeof(MoveTuning).GetProperty(name)!;
        object changed = Tuning;
        object value = property.GetValue(changed)!;
        property.SetValue(changed, value is bool bit ? !bit :
            float.IsPositiveInfinity((float)value) ? 4f : (float)value == 0 ? 0.01f : (float)value * 0.9f);
        Assert.NotEqual(Profile().Fingerprint, Profile(tuning: (MoveTuning)changed).Fingerprint);
    }

    [Fact]
    public void CapabilityLaunchBoundsAreasCadenceAndBudgetCannotAlias()
    {
        string key = Profile().Fingerprint;
        Assert.NotEqual(key, Profile(water: new(WaterTraversalMode.DryOnly)).Fingerprint);
        Assert.NotEqual(key, Profile(water: new(WaterTraversalMode.WadeOnly)).Fingerprint);
        Assert.NotEqual(key, Profile(water: new(WaterTraversalMode.SurfaceSwimmer, 8)).Fingerprint);
        Assert.NotEqual(key, Profile(boundary: "rect-world-v1/fixture").Fingerprint);
        Assert.NotEqual(key, Profile(areas: new(1, 0)).Fingerprint);
        Assert.NotEqual(key, Profile(areas: new(0, 2)).Fingerprint);
        Assert.NotEqual(key, Profile(dt: 1f / 60).Fingerprint);
        Assert.NotEqual(key, Profile(steps: 65).Fingerprint);
    }
}
