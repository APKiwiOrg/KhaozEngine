using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWaterOpticsRecipeTests
{
    public static TheoryData<string> Ids => [.. RendererPerfWaterOptics.RecipeIds];

    [Fact]
    public void Recipe_ids_list_the_six_scenes_in_order()
    {
        Assert.Equal(new[]
        {
            "water-optics-v2/village/off", "water-optics-v2/village/m2-on", "water-optics-v2/village/m5-on",
            "water-optics-v2/outdoors/off", "water-optics-v2/outdoors/m2-on", "water-optics-v2/outdoors/m5-on",
        }, RendererPerfWaterOptics.RecipeIds.AsEnumerable());
        foreach (string id in RendererPerfWaterOptics.RecipeIds)
        {
            using var recipe = RendererPerfRecipe.Create(id, verifyFrozen: false);
            Assert.Equal(id, recipe.Facts.Id);
            Assert.Equal("renderer-full-native-1080/1", recipe.Facts.Profile);
            Assert.Equal(id, RendererPerfRecipe.IdFromVersion(recipe.Facts.Id + "/" + recipe.Facts.Version));
        }
    }

    [Theory]
    [InlineData("village")]
    [InlineData("outdoors")]
    public void Profiles_share_everything_but_the_opt_ins(string scene)
    {
        RendererPerfRecipe[] recipes = [.. RendererPerfWaterOptics.Profiles
            .Select(profile => RendererPerfRecipe.Create(RendererPerfWaterOptics.RecipeId(scene, profile), verifyFrozen: false))];
        try
        {
            foreach (RendererPerfRecipe recipe in recipes)
            {
                Assert.Equal(recipes[0].Facts.GeometrySha256, recipe.Facts.GeometrySha256);
                Assert.Equal(recipes[0].Facts.AssetSha256, recipe.Facts.AssetSha256);
                Assert.Equal(recipes[0].Facts.SequenceSha256, recipe.Facts.SequenceSha256);
                Assert.Equal(recipes[0].Facts.Schedule, recipe.Facts.Schedule);
            }
            Assert.Equal(3, recipes.Select(r => r.Facts.SettingsSha256).Distinct().Count());
            // The serialized settings, and the rest of the construction, are equal once the opt-ins are removed.
            JsonObject[] descriptions = [.. recipes.Select(r => JsonNode.Parse(r.Describe())!.AsObject())];
            string[][] optIns = [[], ["UnderwaterSunlight"], ["UnderwaterSunlight", "WaterColumnLight"]];
            for (int i = 0; i < descriptions.Length; i++)
            {
                JsonObject water = descriptions[i]["Settings"]!["Water"]!.AsObject();
                foreach (string name in new[] { "UnderwaterSunlight", "WaterColumnLight" })
                {
                    Assert.Equal(optIns[i].Contains(name), water.ContainsKey(name));
                    if (water[name] is { } value) Assert.True(value.GetValue<bool>());
                    water.Remove(name);
                }
                descriptions[i].Remove("Id");
                descriptions[i].Remove("SettingsSha256");
            }
            foreach (JsonObject description in descriptions)
                Assert.Equal(descriptions[0].ToJsonString(), description.ToJsonString());
        }
        finally
        {
            foreach (RendererPerfRecipe recipe in recipes) recipe.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Profile_values_match_the_approval(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id, verifyFrozen: false);
        var scene = Assert.IsAssignableFrom<IRendererPerfWaterOpticsScene>(recipe);
        Assert.EndsWith("/" + RendererPerfWaterOptics.Id(scene.Profile), id, StringComparison.Ordinal);
        WaterSettings water = scene.WaterSettings;
        var color = new Color(.28f, .30f, .18f, 1f);
        Assert.Equal(color, water.WaterColor);
        Assert.Equal(1.5f, water.Clarity);
        Assert.Equal(.03f, water.UnderwaterPenumbraPerMetre);
        Vector3 extinction = WaterColumnOptics.Derive(color, 1.5f).Extinction;
        Assert.Equal(new Color(extinction.X, extinction.Y, extinction.Z, 0f), water.AbsorptionPerMetre);
        (bool sunlight, bool column) = scene.Profile switch
        {
            RendererPerfWaterOpticsProfile.Off => (false, false),
            RendererPerfWaterOpticsProfile.M2On => (true, false),
            _ => (true, true),
        };
        Assert.Equal(sunlight, water.UnderwaterSunlight);
        Assert.Equal(column, water.WaterColumnLight);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Pool_holds_receivers_casters_and_floaters(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id, verifyFrozen: false);
        var scene = (IRendererPerfWaterOpticsScene)recipe;
        RendererPerfWaterOpticsPool pool = scene.Pool;
        // Every plane the recipe describes carries the pool floor.
        var planes = new List<JsonObject>();
        CollectPlanes(JsonNode.Parse(recipe.Describe())!["Geometry"], planes);
        Assert.NotEmpty(planes);
        Assert.All(planes, plane =>
        {
            Assert.Equal(.25f, plane["SurfaceY"]!.GetValue<float>());
            Assert.Equal(-1.25f, plane["FloorY"]!.GetValue<float>());
        });
        Assert.Equal(-1.25f, pool.Plane.FloorY);
        Assert.Equal(1.5f, pool.Plane.SurfaceY - pool.Plane.FloorY!.Value, 5);
        // The wave clearance is the active wave source's amplitude bound plus 0.15 m.
        float bound = id.Contains("/village/", StringComparison.Ordinal)
            ? pool.Plane.Look!.SwellAmplitude!.Value
            : RendererPerfWaterOpticsPool.FftPeakDisplacement(scene.WaterSettings.SeaState);
        Assert.Equal(bound, pool.WaveBound);
        Assert.Equal(bound + .15f, pool.Clearance, 6);
        float clearTop = pool.Plane.SurfaceY - pool.Clearance;

        var rocks = pool.Props.Where(p => p.Kind == RendererPerfPoolPropKind.Rock).ToArray();
        var casters = pool.Props.Where(p => p.Kind == RendererPerfPoolPropKind.Caster).ToArray();
        var floaters = pool.Props.Where(p => p.Kind == RendererPerfPoolPropKind.Floater).ToArray();
        Assert.Equal((24, 12, 12), (rocks.Length, casters.Length, floaters.Length));
        Assert.All(pool.Props, prop =>
        {
            Assert.InRange(prop.Min.X, pool.MinX, pool.MaxX);
            Assert.InRange(prop.Max.X, pool.MinX, pool.MaxX);
            Assert.InRange(prop.Min.Z, pool.ShoreZ, pool.FarZ);
            Assert.InRange(prop.Max.Z, pool.ShoreZ, pool.FarZ);
        });
        Assert.All(rocks, rock =>
        {
            Assert.Equal(pool.BedY(rock.Centre.Z), rock.Min.Y, 4);
            Assert.True(rock.Max.Y <= clearTop, $"Rock top {rock.Max.Y} is above {clearTop}.");
        });
        Assert.All(casters, caster =>
        {
            Assert.True(caster.Max.Y <= clearTop, $"Caster top {caster.Max.Y} is above {clearTop}.");
            Assert.True(caster.Min.Y > pool.BedY(caster.Min.Z), "A caster reaches the bed.");
        });
        Assert.All(floaters, floater =>
        {
            Assert.True(floater.Max.Y >= pool.Plane.SurfaceY + pool.Clearance, "A floater does not clear the crests.");
            Assert.True(floater.Min.Y <= clearTop, "A floater does not clear the troughs.");
            Assert.True(floater.Min.Y > pool.BedY(floater.Min.Z), "A floater reaches the bed.");
        });
        // Every prop stays on screen in every measured frame.
        foreach (int index in RendererPerfRecipe.MeasuredIndices)
        {
            Matrix4x4 viewProjection = recipe.Frame(index).ViewProjection;
            Assert.All(pool.Props, prop =>
            {
                foreach (Vector3 corner in new[] { prop.Min, prop.Max, new(prop.Min.X, prop.Min.Y, prop.Max.Z),
                    new(prop.Max.X, prop.Max.Y, prop.Min.Z) })
                {
                    Vector4 clip = Vector4.Transform(new Vector4(corner, 1f), viewProjection);
                    Assert.True(MathF.Abs(clip.X) <= 1f && MathF.Abs(clip.Y) <= 1f, $"A {prop.Kind} leaves frame {index}.");
                }
            });
        }
        for (int a = 0; a < pool.Props.Count; a++)
            for (int b = a + 1; b < pool.Props.Count; b++)
            {
                RendererPerfPoolProp p = pool.Props[a], q = pool.Props[b];
                Assert.False(p.Min.X < q.Max.X && q.Min.X < p.Max.X && p.Min.Z < q.Max.Z && q.Min.Z < p.Max.Z,
                    $"Props {a} and {b} overlap.");
            }
    }

    [Theory]
    [InlineData("village")]
    [InlineData("outdoors")]
    public void Water_covers_about_half_the_frame(string scene)
    {
        using var recipe = RendererPerfRecipe.Create(
            RendererPerfWaterOptics.RecipeId(scene, RendererPerfWaterOpticsProfile.Off), verifyFrozen: false);
        WaterPlane plane = ((IRendererPerfWaterOpticsScene)recipe).Pool.Plane;
        Vector3[] corners =
        [
            new(plane.CenterX - plane.HalfExtentX, plane.SurfaceY, plane.CenterZ - plane.HalfExtentZ),
            new(plane.CenterX + plane.HalfExtentX, plane.SurfaceY, plane.CenterZ - plane.HalfExtentZ),
            new(plane.CenterX + plane.HalfExtentX, plane.SurfaceY, plane.CenterZ + plane.HalfExtentZ),
            new(plane.CenterX - plane.HalfExtentX, plane.SurfaceY, plane.CenterZ + plane.HalfExtentZ),
        ];
        foreach (int index in RendererPerfRecipe.MeasuredIndices)
        {
            Matrix4x4 viewProjection = recipe.Frame(index).ViewProjection;
            double coverage = RendererPerfFrameCoverage.Fraction(corners, viewProjection);
            Assert.True(coverage is >= .45 and <= .60, $"Frame {index} water coverage {coverage:F4} is outside 0.45 to 0.60.");
        }
    }

    [Theory]
    [InlineData("village")]
    [InlineData("outdoors")]
    public void Expected_counts_match_the_engine_cull(string scene)
    {
        using var recipe = RendererPerfRecipe.Create(
            RendererPerfWaterOptics.RecipeId(scene, RendererPerfWaterOpticsProfile.Off), verifyFrozen: false);
        var village = (RendererPerfVillageScene)recipe;
        RendererPerfRigidBounds[] terrain = [.. village.RigidBounds().Where(draw => draw.Ground)];
        RendererPerfRigidBounds[] rigid = [.. village.RigidBounds()
            .Concat(((IRendererPerfWaterOpticsScene)recipe).Pool.RigidBounds())];
        JsonObject expected = JsonNode.Parse(recipe.Describe())!["Expected"]!.AsObject();
        int Read(string name) => expected[name]!.GetValue<int>();
        Assert.Equal(terrain.Length, Read("TerrainIntended"));
        Assert.True(Read("TerrainCulled") > 0, "The v2 camera culls part of the terrain.");
        Assert.Equal(terrain.Length, Read("TerrainVisible") + Read("TerrainCulled"));
        Assert.Equal(rigid.Length, Read("RigidIntended"));
        using var rig = new HeadlessSceneRig();
        recipe.ResetBlock(rig.Scene);
        foreach (int index in RendererPerfRecipe.WarmupIndices.Concat(RendererPerfRecipe.MeasuredIndices))
        {
            Matrix4x4 viewProjection = recipe.Frame(index).ViewProjection;
            rig.Frame(RendererPerfRecipe.Width, RendererPerfRecipe.Height, s => recipe.Queue(s, index));
            // The recipe's rule reproduces the engine's cull exactly, and the frozen counts bound it.
            Assert.Equal(RendererPerfMainPassVisibility.Visible(rigid, viewProjection), rig.Scene.DrawnInstances);
            Assert.Equal(rigid.Length, rig.Scene.DrawnInstances + rig.Scene.CulledInstances);
            Assert.InRange(rig.Scene.DrawnInstances, Read("RigidVisibleMin"), Read("RigidVisibleMax"));
            Assert.InRange(rig.Scene.CulledInstances, Read("RigidCulledMin"), Read("RigidCulledMax"));
            Assert.Equal(Read("TerrainVisible"), RendererPerfMainPassVisibility.Visible(terrain, viewProjection));
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Profiles_reach_their_routes(string id)
    {
        using var device = new PostTraceDevice();
        using var recipe = RendererPerfRecipe.Create(id);
        var profile = ((IRendererPerfWaterOpticsScene)recipe).Profile;
        var trace = new GeometryTraceCommands(device.Factory.CreateCommandList());
        var observer = new RendererPerfGeometryTraceTests.GeometryObserver(trace);
        using var work = new RendererPerfSceneWorkload(device, observer, Identity(recipe, "S0"), (_, _) => { });
        work.PrepareCold(-192);
        work.Prepare(-192);
        trace.Begin();
        work.Record(trace, -192);
        trace.End();
        Scene3D scene = work.Scene;
        Assert.Equal(1, scene.WaterPlaneCount);
        WaterVolumeClusterDiagnostics volumes = scene.WaterVolumeClusters;
        WaterColumnDiagnostics column = scene.WaterColumnLightDiagnostics;
        Assert.Equal(profile == RendererPerfWaterOpticsProfile.Off ? 0 : 1, volumes.RecordCount);
        Assert.Equal(0, volumes.DroppedRecords);
        if (profile != RendererPerfWaterOpticsProfile.Off) Assert.True(volumes.ReferenceCount > 0);
        bool columns = profile == RendererPerfWaterOpticsProfile.M5On;
        Assert.Equal(columns ? 1 : 0, column.OptedInPlanes);
        Assert.Equal(columns, column.CopyRan);
        Assert.Equal(columns, column.CopyPath != WaterColumnCopyPath.None);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Frozen_layout_fits_the_scope_capacity(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id);
        RendererPerfPassDefinition[] passes = RendererPerfFrozenLayout.Create(Identity(recipe, "S0"));
        Assert.InRange(passes.Length, 1, 255);
        Assert.Contains(passes, pass => pass.Id == "water");
    }

    static RendererPerfRecipeIdentity Identity(RendererPerfRecipe recipe, string sceneId)
    {
        RendererPerfRecipeFacts facts = recipe.Facts;
        return new RendererPerfRecipeIdentity(sceneId, facts.Id + "/" + facts.Version, facts.Profile, facts.RecipeSha256,
            facts.SettingsSha256, new string('b', 64), facts.GeometrySha256, facts.AssetSha256);
    }

    static void CollectPlanes(JsonNode? node, List<JsonObject> planes)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("SurfaceY") && obj.ContainsKey("HalfExtentX")) planes.Add(obj);
            foreach (var pair in obj) CollectPlanes(pair.Value, planes);
        }
        else if (node is JsonArray array)
            foreach (JsonNode? item in array) CollectPlanes(item, planes);
    }
}
