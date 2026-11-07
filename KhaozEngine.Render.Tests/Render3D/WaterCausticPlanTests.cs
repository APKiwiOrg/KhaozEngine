using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WaterCausticPlanTests
{
    [Fact]
    public void Disjoint_rectangles_with_one_look_share_a_layer()
    {
        var look = new WaterLook();
        var plan = Build([new(-1f, 0f, 0f, 1f, look: look), new(1f, 0f, 0f, 1f, look: look)]);

        Assert.Equal(new WaterCausticDiagnostics(2, 2, 0, 0, 1), plan.Diagnostics);
        Assert.Equal(1, plan.LayerOfPlane(0));
        Assert.Equal(1, plan.LayerOfPlane(1));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.2f)]
    public void Touching_rectangles_with_different_looks_take_separate_layers(float gap)
    {
        var plan = Build([new(-1f, 0f, 0f, 1f, look: new WaterLook()),
            new(1f + gap, 0f, 0f, 1f, look: new WaterLook())]);

        Assert.Equal(2, plan.LayerCount);
        Assert.Equal(1, plan.LayerOfPlane(0));
        Assert.Equal(2, plan.LayerOfPlane(1));
    }

    [Fact]
    public void Overlapping_rectangles_take_separate_layers()
    {
        var look = new WaterLook();
        var plan = Build([new(0f, 0f, 0f, 2f, look: look), new(1f, 0f, 0f, 2f, look: look),
            new(10f, 0f, 0f, 1f, look: new WaterLook())]);

        Assert.Equal(2, plan.LayerCount);
        Assert.Equal(1, plan.LayerOfPlane(0));
        Assert.Equal(2, plan.LayerOfPlane(1));
        Assert.Equal(1, plan.LayerOfPlane(2));
    }

    [Fact]
    public void Fifth_conflicting_body_overflows_and_is_counted()
    {
        WaterPlane[] planes = [new(0f, 0f, 0f, 2f), new(0f, 1f, 0f, 2f), new(0f, 2f, 0f, 2f),
            new(0f, 3f, 0f, 2f), new(0f, 4f, 0f, 2f)];
        var plan = Build(planes);

        Assert.Equal(new WaterCausticDiagnostics(5, 4, 0, 1, 4), plan.Diagnostics);
        Assert.Equal(4, plan.Bodies.Length);
        for (int i = 0; i < 4; i++) Assert.Equal(i + 1, plan.LayerOfPlane(i));
        Assert.Equal(0, plan.LayerOfPlane(4));
        Assert.Equal(0f, plan.DepthOfPlane(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fft_opt_in_is_counted_and_never_drawn(bool sceneIsFft)
    {
        var scene = Settings();
        if (sceneIsFft) scene.WaveSource = WaterWaveSource.FftOcean;
        var fft = new WaterLook { WaveSource = WaterWaveSource.FftOcean, CausticStrength = float.NaN };
        var procedural = sceneIsFft ? new WaterLook { WaveSource = WaterWaveSource.Procedural } : null;
        var plan = Build([new(0f, 0f, 0f, 2f, look: fft), new(0f, 0f, 0f, 2f, look: procedural)], scene);

        Assert.Equal(new WaterCausticDiagnostics(2, 1, 1, 0, 1), plan.Diagnostics);
        Assert.Equal(0, plan.LayerOfPlane(0));
        Assert.Equal(0f, plan.DepthOfPlane(0));
        Assert.Equal(1, plan.LayerOfPlane(1));
    }

    [Fact]
    public void Huge_body_stays_within_the_window()
    {
        var plan = Build([new(0f, 0f, 0f, 50000f)]);
        Assert.Single(plan.Bodies.ToArray());
        Span<Vector3> instances = stackalloc Vector3[1024];
        int count = WaterCausticPlan.WritePatches(plan.Bodies[0], plan.Window, instances);

        Assert.Equal(1024, count);
        var unclipped = plan.Bodies[0] with { RectMin = new(-50000f), RectMax = new(50000f) };
        Assert.Equal(count, WaterCausticPlan.WritePatches(unclipped, plan.Window, instances));
        int[] perSlice = new int[4];
        for (int i = 0; i < count; i++)
        {
            Assert.InRange(instances[i].X, 0f, 480f);
            Assert.InRange(instances[i].Y, 0f, 480f);
            Assert.Equal(0f, instances[i].X % 32f);
            Assert.Equal(0f, instances[i].Y % 32f);
            Assert.InRange(instances[i].Z, 0f, 3f);
            perSlice[(int)instances[i].Z]++;
        }
        Assert.All(perSlice, patches => Assert.Equal(256, patches));
    }

    [Theory]
    [InlineData(1f, 1f, 2f, 2f, 1, 0f, 0f)]
    [InlineData(3.5f, 1f, 4.5f, 2f, 2, 32f, 0f)]
    [InlineData(3.5f, 3.5f, 4.5f, 4.5f, 4, 32f, 32f)]
    [InlineData(4f, 4f, 8f, 8f, 1, 32f, 32f)]
    public void Patches_cover_each_touched_window_local_patch_once(float minX, float minZ, float maxX, float maxZ,
        int patches, float cornerX, float cornerZ)
    {
        var window = new WaterSurfaceWindow(100f, -50f);
        var body = new WaterCausticBody(0, 0, 1f, 2f, new(100f + minX, -50f + minZ), new(100f + maxX, -50f + maxZ));
        Span<Vector3> instances = stackalloc Vector3[1024];
        int count = WaterCausticPlan.WritePatches(body, window, instances);

        Assert.Equal(patches * 4, count);
        var written = instances[..count].ToArray();
        Assert.Equal(count, written.ToHashSet().Count);
        Assert.Contains(new Vector3(cornerX, cornerZ, 3f), written);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 1f)]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(1000f, 1000f, 1f, 1f)]
    [InlineData(33f, 0f, 1f, 1f)]
    public void Degenerate_or_outside_rectangle_takes_no_layer(float x, float z, float halfX, float halfZ)
    {
        var plan = Build([new(x, 0f, z, halfX, halfZ)]);

        Assert.False(plan.Active);
        Assert.Equal(new WaterCausticDiagnostics(1, 0, 0, 0, 0), plan.Diagnostics);
        Assert.Equal(0, plan.LayerOfPlane(0));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    public void Sun_at_or_below_horizon_plans_nothing(float elevationDegrees)
    {
        var scene = Settings();
        WaterPlane[] planes = [new(0f, 0f, 0f, 2f),
            new(0f, 0f, 0f, 2f, look: new WaterLook { WaveSource = WaterWaveSource.FftOcean })];
        var plan = Build(planes, scene);
        float angle = elevationDegrees * MathF.PI / 180f;
        Rebuild(plan, planes, scene, new Vector3(MathF.Cos(angle), -MathF.Sin(angle), 0f));

        Assert.False(plan.Active);
        Assert.Equal(new WaterCausticDiagnostics(2, 0, 1, 0, 0), plan.Diagnostics);
        Assert.Equal(0, plan.Bodies.Length);
        Assert.Equal(0, plan.LayerOfPlane(0));
        Assert.Equal(0f, plan.DepthOfPlane(0));
    }

    [Fact]
    public void Near_horizontal_sun_stays_finite()
    {
        var plan = new WaterCausticPlan();
        float angle = .5f * MathF.PI / 180f;
        Rebuild(plan, [new(0f, 0f, 0f, 2f)], Settings(), new Vector3(MathF.Cos(angle), -MathF.Sin(angle), 0f));

        Assert.True(plan.Active);
        Assert.Equal(1, plan.Bodies.Length);
        Assert.True(float.IsFinite(plan.DepthOfPlane(0)));
        Assert.InRange(plan.DepthOfPlane(0), 1f, 32f);
    }

    [Theory]
    [InlineData(2f, 1.5f)]
    [InlineData(10f, 1f)]
    [InlineData(.01f, 32f)]
    [InlineData(0f, 32f)]
    public void Slice_depth_follows_attenuation(float mean, float expected)
    {
        Assert.Equal(expected, WaterCausticPlan.SliceDepth(new Vector3(mean * .5f, mean, mean * 1.5f)));
    }

    [Fact]
    public void Body_depth_and_strength_follow_the_resolved_look()
    {
        var scene = Settings();
        scene.WaterColumnLight = true;
        scene.Clarity = 4f;
        scene.AbsorptionPerMetre = new Color(1f, 1f, 1f, 0f);
        var river = new WaterPlane(0f, 0f, 0f, 2f, look: TileWaterLooks.River);
        var look = new WaterLook
        {
            WaterColumnLight = false, CausticStrength = 7f, AbsorptionPerMetre = new Color(.1f, .2f, .3f, 0f),
        };
        var plan = Build([river, new(10f, 0f, 0f, 2f, look: look), new(-10f, 0f, 0f, 2f)], scene);

        Assert.Equal(3, plan.Bodies.Length);
        Assert.Equal(1.5f, plan.DepthOfPlane(0), 5);
        Assert.Equal(15f, plan.DepthOfPlane(1), 5);
        Assert.Equal(4f, plan.DepthOfPlane(2));
        Assert.Equal(7f, plan.Bodies[1].Strength);
    }

    [Fact]
    public void Window_uses_the_nearest_participating_rectangle_as_its_reference_plane()
    {
        var plan = new WaterCausticPlan();
        WaterPlane[] planes = [new(100f, 5f, 0f, 100f), new(1f, 0f, 0f, .5f),
            new(0f, 9f, 0f, 1f, look: new WaterLook { UnderwaterCaustics = false })];
        plan.Build(planes, Settings(), -Vector3.UnitY, new(0f, 10f, 0f), Vector3.Normalize(new(0f, -1f, -1f)),
            Matrix4x4.Identity, true, Vector3.Zero);

        Assert.True(plan.Active);
        Assert.InRange(plan.Window.MinZ + 32f, -5.125f, -4.875f);
    }

    [Fact]
    public void Resource_demand_counts_participants_outside_the_window()
    {
        WaterPlane[] planes = [new(10000f, 0f, 0f, 1f), new(20000f, 0f, 0f, 1f), new(30000f, 0f, 0f, 1f)];
        Assert.Equal(4, WaterCausticPlan.RequiredLayers(planes, Settings(), new WaterSettings()));
        Assert.False(Build(planes).Active);
    }

    [Theory]
    [InlineData(false, 1f, WaterWaveSource.Procedural, false)]
    [InlineData(true, 0f, WaterWaveSource.Procedural, false)]
    [InlineData(true, 1f, WaterWaveSource.FftOcean, false)]
    [InlineData(true, 1f, WaterWaveSource.Procedural, true)]
    public void Participation_depends_only_on_resolved_opt_in_strength_and_source(bool enabled, float strength,
        WaterWaveSource source, bool expected)
    {
        var settings = new WaterSettings { UnderwaterCaustics = enabled, CausticStrength = strength, WaveSource = source };
        var plane = new WaterPlane(100000f, 0f, 100000f, 1f);
        Assert.Equal(expected, WaterCausticPlan.Participates(plane, settings));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 4)]
    public void Required_layers_never_undercounts_the_frame_plan(int participants, int expected)
    {
        var scene = Settings();
        var planes = new WaterPlane[participants + 3];
        for (int i = 0; i < participants; i++) planes[i] = new(0f, i, 0f, 2f);
        planes[participants] = new(0f, 0f, 0f, 2f, look: new WaterLook { UnderwaterCaustics = false });
        planes[participants + 1] = new(0f, 0f, 0f, 2f, look: new WaterLook { WaveSource = WaterWaveSource.FftOcean });
        planes[participants + 2] = new(0f, 0f, 0f, 2f, look: new WaterLook { CausticStrength = 0f });
        int required = WaterCausticPlan.RequiredLayers(planes, scene, new WaterSettings());
        var plan = Build(planes, scene);

        Assert.Equal(expected, required);
        Assert.InRange(plan.LayerCount, 0, required);
        Assert.Equal(participants, plan.Diagnostics.DrawnPlanes + plan.Diagnostics.OverflowPlanes);
        Assert.Equal(participants + 2, plan.Diagnostics.OptedInPlanes);
        Assert.Equal(1, plan.Diagnostics.FftPlanes);
    }

    [Fact]
    public void Rebuilding_a_smaller_queue_clears_old_plane_assignments()
    {
        var scene = Settings();
        var plan = Build([new(0f, 0f, 0f, 2f), new(0f, 1f, 0f, 2f)], scene);
        Assert.Equal(2, plan.LayerOfPlane(1));
        Rebuild(plan, [new(0f, 0f, 0f, 2f, look: new WaterLook { CausticStrength = 0f })], scene, -Vector3.UnitY);

        Assert.False(plan.Active);
        Assert.Equal(new WaterCausticDiagnostics(1, 0, 0, 0, 0), plan.Diagnostics);
        Assert.Equal(0, plan.Bodies.Length);
        Assert.Equal(0, plan.LayerOfPlane(0));
        Assert.Equal(0, plan.LayerOfPlane(1));
        Assert.Equal(0f, plan.DepthOfPlane(1));
    }

    [Fact]
    public void Rebuilding_a_steady_plan_allocates_nothing()
    {
        var scene = Settings();
        var scratch = new WaterSettings();
        WaterPlane[] planes = [new(-1f, 0f, 0f, 1f, look: TileWaterLooks.River),
            new(1f, 0f, 0f, 1f, look: TileWaterLooks.River), new(0f, 1f, 0f, 2f)];
        var plan = Build(planes, scene);
        void Frame()
        {
            WaterCausticPlan.RequiredLayers(planes, scene, scratch);
            Rebuild(plan, planes, scene, -Vector3.UnitY);
            Span<Vector3> instances = stackalloc Vector3[1024];
            foreach (var body in plan.Bodies) WaterCausticPlan.WritePatches(body, plan.Window, instances);
        }
        for (int i = 0; i < 4; i++) Frame();
        long allocatedBytes;
        using (AllocationRegion.Start("Steady caustic window, plan and patches"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Frame();
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocatedBytes);
        Assert.Equal(3, plan.Bodies.Length);
    }

    static WaterSettings Settings() => new() { UnderwaterCaustics = true };

    static WaterCausticPlan Build(WaterPlane[] planes, WaterSettings? scene = null)
    {
        var plan = new WaterCausticPlan();
        Rebuild(plan, planes, scene ?? Settings(), -Vector3.UnitY);
        return plan;
    }

    static void Rebuild(WaterCausticPlan plan, WaterPlane[] planes, WaterSettings scene, Vector3 lightDirection) =>
        plan.Build(planes, scene, lightDirection, new Vector3(0f, 10f, 0f), -Vector3.UnitY,
            Matrix4x4.Identity, true, Vector3.Zero);
}
